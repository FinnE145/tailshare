using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Tailshare.Core;

public enum HoldOutcome { PickedUp, NotPickedUp, Failed, Cancelled }

/// <summary>
/// Items this computer is holding for a phone or iPad to fetch. Lives in the host, not the receiver,
/// so a hold survives the receiver being rebound; the receiver only serves what is in here.
/// </summary>
public sealed class PickupStore
{
    private readonly ConcurrentDictionary<string, HeldItem> _items = new();
    private readonly TimeSpan _lifetime;
    private readonly string _tempDirectory;
    private long _sequence;

    /// <param name="lifetime">How long an item waits to be picked up; shortened by tests.</param>
    /// <param name="tempDirectory">Where a bundle's zip is made; the system temp folder unless a test says otherwise.</param>
    public PickupStore(TimeSpan? lifetime = null, string? tempDirectory = null)
    {
        _lifetime = lifetime ?? Protocol.HoldLifetime;
        _tempDirectory = tempDirectory ?? Path.GetTempPath();
    }

    /// <summary>How long an item waits to be picked up.</summary>
    public TimeSpan Lifetime => _lifetime;

    /// <summary>What is waiting for <paramref name="targetId"/>, oldest first. Nothing held for anyone else.</summary>
    public IReadOnlyList<PickupListing> ListFor(string targetId) =>
        _items.Values
            .Where(i => i.TargetId == targetId && i.IsWaiting)
            .OrderBy(i => i.Sequence)
            .Select(i => new PickupListing(i.Token, i.Kind, i.ListName, i.Bytes))
            .ToList();

    /// <summary>
    /// Starts the one download of <paramref name="token"/>, or null if it doesn't exist, ran out, was
    /// already taken, or is held for someone else. Taking it removes it from <see cref="ListFor"/>.
    /// </summary>
    internal HeldItem? Begin(string token, string callerId) =>
        _items.TryGetValue(token, out var item) && item.TargetId == callerId && item.TryBegin() ? item : null;

    /// <summary>Ends every hold as cancelled and deletes their temp files, for the app quitting.</summary>
    public void CancelAll()
    {
        foreach (var item in _items.Values.ToList()) item.Cancel();
    }

    /// <summary>
    /// Holds <paramref name="content"/> for the device <paramref name="targetId"/> until it is picked
    /// up, the lifetime runs out, or <paramref name="ct"/> is cancelled. Returns how it ended.
    /// </summary>
    public async Task<HoldOutcome> HoldAsync(string targetId, SendContent content, IProgress<SendProgress>? progress, CancellationToken ct)
    {
        string? zipPath = null;
        HeldItem? item = null;
        try
        {
            Func<Stream> open;
            OfferKind kind;
            string? listName, fileName;
            long bytes;
            string contentType = "application/octet-stream";

            switch (content)
            {
                case SendContent.Text t:
                    var data = Encoding.UTF8.GetBytes(t.Value);
                    (kind, listName, fileName, bytes) = (OfferKind.Text, null, null, data.Length);
                    contentType = "text/plain; charset=utf-8";
                    open = () => new MemoryStream(data, writable: false);
                    break;

                case SendContent.Paths p when p.Items.Count == 1 && File.Exists(p.Items[0]):
                    var path = p.Items[0];
                    var name = Path.GetFileName(path);
                    (kind, listName, fileName, bytes) = (OfferKind.File, name, name, new FileInfo(path).Length);
                    // Delete sharing: the user's own file must not be locked against deleting or moving while a phone reads it.
                    open = () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    break;

                case SendContent.Paths p:
                    progress?.Report(new SendProgress(SendStage.Preparing, 0, 0));
                    var zip = zipPath = Path.Combine(_tempDirectory, $"tailshare-{Guid.NewGuid():N}.zip");
                    var info = await Task.Run(() => Bundles.Create(p.Items, zip, ct), ct);
                    (kind, listName, fileName, bytes) = (OfferKind.Bundle, info.Name, Names.Sanitize(info.Name) + ".zip", new FileInfo(zip).Length);
                    open = () => new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    break;

                default:
                    return HoldOutcome.Failed;
            }

            item = new HeldItem(this, NewToken(), targetId, Interlocked.Increment(ref _sequence),
                kind, listName, fileName, bytes, contentType, open, zipPath, progress);
            // From here the item owns the temp zip and deletes it when it ends.
            zipPath = null;
            _items[item.Token] = item;
            progress?.Report(new SendProgress(SendStage.Waiting, 0, 0));

            using var expiry = new CancellationTokenSource(_lifetime);
            using var onExpiry = expiry.Token.Register(item.Expire);
            using var onCancel = ct.Register(item.Cancel);
            return await item.Done;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return HoldOutcome.Cancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return HoldOutcome.Failed;
        }
        finally
        {
            // Only reached with a path still ours when the zip was made but no item took it over.
            if (zipPath is not null) HeldItem.DeleteQuietly(zipPath);
            item?.Cancel();
        }
    }

    internal void Remove(string token) => _items.TryRemove(token, out _);

    /// <summary>Items still in the store, however they stand; for tests, since an ended item must not linger (a held text would stay in memory).</summary>
    internal int HeldCount => _items.Count;

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>One held item and where it is in its life: waiting, being downloaded, or over.</summary>
internal sealed class HeldItem
{
    private enum State { Waiting, Downloading, Finished }

    private readonly object _gate = new();
    private readonly PickupStore _store;
    private readonly Func<Stream> _open;
    private readonly string? _tempZip;
    private readonly IProgress<SendProgress>? _progress;
    private readonly CancellationTokenSource _download = new();
    private readonly TaskCompletionSource<HoldOutcome> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private State _state = State.Waiting;

    public HeldItem(PickupStore store, string token, string targetId, long sequence, OfferKind kind, string? listName,
        string? fileName, long bytes, string contentType, Func<Stream> open, string? tempZip, IProgress<SendProgress>? progress)
    {
        _store = store;
        Token = token;
        TargetId = targetId;
        Sequence = sequence;
        Kind = kind;
        ListName = listName;
        FileName = fileName;
        Bytes = bytes;
        ContentType = contentType;
        _open = open;
        _tempZip = tempZip;
        _progress = progress;
    }

    public string Token { get; }
    public string TargetId { get; }
    public long Sequence { get; }
    public OfferKind Kind { get; }
    public string? ListName { get; }
    /// <summary>The download's file name; null for text.</summary>
    public string? FileName { get; }
    public long Bytes { get; }
    public string ContentType { get; }
    public Task<HoldOutcome> Done => _done.Task;

    /// <summary>Cancelled when the hold is cancelled during the download.</summary>
    public CancellationToken Cancelled => _download.Token;

    public bool IsWaiting
    {
        get { lock (_gate) return _state == State.Waiting; }
    }

    /// <summary>Takes the item for its one download. False if it is no longer waiting.</summary>
    public bool TryBegin()
    {
        lock (_gate)
        {
            if (_state != State.Waiting) return false;
            _state = State.Downloading;
            return true;
        }
    }

    public Stream Open() => _open();

    public void Progress(long sent) => _progress?.Report(new SendProgress(SendStage.Sending, sent, Bytes));

    /// <summary>The download ended: it reached the end of the body, or it didn't.</summary>
    public void Finish(bool delivered) => End(delivered ? HoldOutcome.PickedUp : HoldOutcome.Failed, State.Downloading);

    /// <summary>The lifetime ran out. A download that has started is left to finish.</summary>
    public void Expire() => End(HoldOutcome.NotPickedUp, State.Waiting);

    public void Cancel() => End(HoldOutcome.Cancelled, null);

    // The first ending wins; later ones (a cancel after the download finished) change nothing. The
    // state check and the change are one step, so an expiry can't end an item a download just took.
    private void End(HoldOutcome outcome, State? only)
    {
        lock (_gate)
        {
            if (_state == State.Finished || (only is { } required && _state != required)) return;
            _state = State.Finished;
        }

        _store.Remove(Token);
        if (outcome == HoldOutcome.Cancelled) _download.Cancel();
        if (_tempZip is not null) DeleteQuietly(_tempZip);
        _done.TrySetResult(outcome);
    }

    public static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
