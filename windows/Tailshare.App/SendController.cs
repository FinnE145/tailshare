using Tailshare.Core;

namespace Tailshare.App;

/// <param name="Fraction">0 to 1, or null when there is nothing to measure (waiting, preparing).</param>
/// <param name="Busy">A send is in the way: the Send button waits. A hold for a phone is not in the way once it is waiting.</param>
/// <param name="Cancellable">Cancel does something: the send or the hold is still going.</param>
internal sealed record SendStatus(string Text, double? Fraction, bool Busy, bool Cancellable);

/// <summary>
/// Runs sends for a window and turns their progress into status lines. One computer send at a time, but a
/// hold for a phone only stands waiting, so another send can start while it does. The status line and
/// Cancel always belong to the most recent send; an older one that ends later does not touch them.
/// </summary>
internal sealed class SendController(AppHost host)
{
    private CancellationTokenSource? _current;
    private int _generation;
    private bool _busy;

    public event Action<SendStatus>? Changed;

    public bool Busy => _busy;

    // Starts a send: it is now the one the status line and Cancel follow.
    private int Begin(CancellationTokenSource cts)
    {
        _current = cts;
        _busy = true;
        return ++_generation;
    }

    private void Emit(int generation, SendStatus status)
    {
        if (generation != _generation) return;
        _busy = status.Busy;
        Changed?.Invoke(status);
    }

    public async Task<SendOutcome> SendAsync(PeerInfo to, SendContent content)
    {
        var device = to.Identity.Name;
        using var cts = new CancellationTokenSource();
        var generation = Begin(cts);
        var ended = false;
        Emit(generation, new SendStatus($"Waiting for {device}", null, true, true));

        // Progress<T> posts to the UI thread, where this controller lives. A report still queued when the
        // send ends would otherwise land after the result and bring the progress line back.
        var progress = new Progress<SendProgress>(p =>
        {
            if (ended) return;
            Emit(generation, p.Stage switch
            {
                SendStage.Preparing => new SendStatus("Preparing", null, true, true),
                SendStage.Waiting => new SendStatus($"Waiting for {device}", null, true, true),
                _ => new SendStatus(
                    $"Sending to {device}: {Notices.FormatSize(p.BytesSent)} of {Notices.FormatSize(p.BytesTotal)}",
                    p.BytesTotal == 0 ? 1 : (double)p.BytesSent / p.BytesTotal, true, true),
            });
        });

        SendOutcome outcome;
        using (var sender = new Sender(host.Port))
        {
            outcome = await sender.SendAsync(to, content, progress, cts.Token);
        }
        ended = true;
        if (generation == _generation) _current = null;

        Emit(generation, new SendStatus(ResultText(outcome, device), outcome == SendOutcome.Sent ? 1 : null, false, false));
        return outcome;
    }

    /// <summary>
    /// Holds the content for a phone or iPad to pick up, and returns when the hold ends. The notification
    /// for how it ended is raised here, since the window may be gone or looking at a newer send by then.
    /// </summary>
    public async Task<HoldOutcome> HoldAsync(Identity to, SendContent content)
    {
        var device = to.Name;
        using var cts = new CancellationTokenSource();
        var generation = Begin(cts);
        var ended = false;
        // Busy until the hold is really waiting: a second click before then would hold the same thing twice.
        Emit(generation, new SendStatus(Notices.HoldWaiting(device), null, true, true));

        var progress = new Progress<SendProgress>(p =>
        {
            if (ended) return;
            Emit(generation, p.Stage switch
            {
                SendStage.Preparing => new SendStatus("Preparing", null, true, true),
                SendStage.Waiting => new SendStatus(Notices.HoldWaiting(device), null, false, true),
                _ => new SendStatus(
                    $"{device} is picking up: {Notices.FormatSize(p.BytesSent)} of {Notices.FormatSize(p.BytesTotal)}",
                    p.BytesTotal == 0 ? 1 : (double)p.BytesSent / p.BytesTotal, false, true),
            });
        });

        var outcome = await host.Pickups.HoldAsync(to.Id, content, progress, cts.Token);
        ended = true;
        if (generation == _generation) _current = null;

        Emit(generation, new SendStatus(Notices.HoldStatus(outcome, device), outcome == HoldOutcome.PickedUp ? 1 : null, false, false));
        if (Notices.HoldNotice(outcome, device) is { } notice) host.Notifier.Show("tailshare", notice);
        return outcome;
    }

    // _current is cleared when its send ends, so this never reaches a source that was already disposed.
    public void Cancel() => _current?.Cancel();

    public static string ResultText(SendOutcome outcome, string device) => outcome switch
    {
        SendOutcome.Sent => "Sent",
        SendOutcome.Declined => $"{device} declined",
        SendOutcome.DidntRespond => $"{device} didn't respond",
        SendOutcome.Cancelled => "Cancelled",
        _ => "Failed",
    };

    /// <summary>The notification text after a share-sheet send.</summary>
    public static string ShareResultText(SendOutcome outcome, string device) => outcome switch
    {
        SendOutcome.Sent => $"Sent to {device}",
        SendOutcome.Declined => $"{device} declined",
        SendOutcome.DidntRespond => $"{device} didn't respond",
        SendOutcome.Cancelled => "Cancelled",
        _ => $"Couldn't send to {device}",
    };
}
