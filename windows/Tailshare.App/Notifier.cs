using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Tailshare.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Tailshare.App;

/// <summary>
/// Every notification, and what clicking them does. It is also the receiver's host: the first-contact
/// prompt is a notification with Accept and Decline, and a big transfer shows a progress notification.
/// </summary>
internal sealed class Notifier(DispatcherQueue ui) : IReceiverHost
{
    private const int MaxPayloads = 100;

    // What a click does is looked up by id, never carried in the notification's arguments: arguments
    // are size-limited, and a path or URL in them would be text the notification could be tampered with.
    private sealed record Payload(PayloadKind Kind, string Value);
    private enum PayloadKind { Select, Folder, Copy, Link }

    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _prompts = new();
    private readonly ConcurrentDictionary<string, Action> _cancels = new();
    private readonly ConcurrentDictionary<string, Payload> _payloads = new();
    private readonly ConcurrentQueue<string> _payloadOrder = new();

    public void Register()
    {
        AppNotificationManager.Default.NotificationInvoked += OnInvoked;
        AppNotificationManager.Default.Register();
    }

    public void Unregister() => AppNotificationManager.Default.Unregister();

    public void Show(string title, string body)
    {
        var builder = new AppNotificationBuilder().AddText(title);
        if (body.Length > 0) builder.AddText(body);
        AppNotificationManager.Default.Show(builder.BuildNotification());
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    // ---- IReceiverHost

    public async Task<bool> AskFirstContactAsync(Identity from, Offer offer, CancellationToken ct)
    {
        var id = NewId();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _prompts[id] = tcs;

        var notice = Notices.FirstContact(from.Name, offer);
        var notification = new AppNotificationBuilder()
            .AddText(notice.Title)
            .AddText(notice.Body)
            .AddButton(new AppNotificationButton("Accept").AddArgument("action", "accept").AddArgument("id", id))
            .AddButton(new AppNotificationButton("Decline").AddArgument("action", "decline").AddArgument("id", id))
            // Reminder keeps it on screen until acted on.
            .SetScenario(AppNotificationScenario.Reminder)
            .BuildNotification();
        notification.Tag = id;
        notification.Group = "prompt";
        AppNotificationManager.Default.Show(notification);

        // Timeout or the sender hanging up: the receiver cancels, and the prompt goes away.
        using var registration = ct.Register(() =>
        {
            tcs.TrySetResult(false);
            _ = AppNotificationManager.Default.RemoveByTagAndGroupAsync(id, "prompt");
        });
        try { return await tcs.Task; }
        finally { _prompts.TryRemove(id, out _); }
    }

    public IIncomingTransfer BeginIncoming(Identity from, Offer offer) =>
        offer.ExpectedBytes > Notices.ProgressThresholdBytes ? new ProgressTransfer(this, from, offer) : new QuietTransfer();

    public void Received(Identity from, Offer offer, ReceivedItem item)
    {
        if (offer.Kind == OfferKind.Text)
        {
            var text = item.Text ?? "";
            var notice = Notices.ReceivedText(from.Name, text);
            var isLink = LinkDetector.TryGetLink(text, out var uri);
            var id = Remember(new Payload(isLink ? PayloadKind.Link : PayloadKind.Copy, isLink ? uri.AbsoluteUri : text));

            var builder = new AppNotificationBuilder().AddText(notice.Title).AddText(notice.Body)
                .AddArgument("action", "open").AddArgument("id", id);
            if (isLink) builder.AddButton(new AppNotificationButton("Copy").AddArgument("action", "copy").AddArgument("id", id));
            AppNotificationManager.Default.Show(builder.BuildNotification());
            return;
        }

        var files = Notices.Received(from.Name, offer);
        var payload = item.Paths.Count == 1
            ? new Payload(PayloadKind.Select, item.Paths[0])
            : new Payload(PayloadKind.Folder, Path.GetDirectoryName(item.Paths[0])!);
        var fileId = Remember(payload);
        var n = new AppNotificationBuilder().AddText(files.Title).AddText(files.Body)
            .AddArgument("action", "open").AddArgument("id", fileId)
            .BuildNotification();
        AppNotificationManager.Default.Show(n);
    }

    private string Remember(Payload p)
    {
        var id = NewId();
        _payloads[id] = p;
        _payloadOrder.Enqueue(id);
        while (_payloadOrder.Count > MaxPayloads && _payloadOrder.TryDequeue(out var old)) _payloads.TryRemove(old, out _);
        return id;
    }

    // ---- clicks (arrive on a background thread)

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue("action", out var action) || !args.Arguments.TryGetValue("id", out var id)) return;

        switch (action)
        {
            case "accept": if (_prompts.TryGetValue(id, out var yes)) yes.TrySetResult(true); break;
            case "decline": if (_prompts.TryGetValue(id, out var no)) no.TrySetResult(false); break;
            case "cancel": if (_cancels.TryGetValue(id, out var cancel)) cancel(); break;
            case "open" or "copy":
                if (_payloads.TryGetValue(id, out var payload)) ui.TryEnqueue(() => _ = Act(payload, action == "copy"));
                break;
        }
    }

    private static async Task Act(Payload p, bool copyOnly)
    {
        if (copyOnly || p.Kind == PayloadKind.Copy)
        {
            var package = new DataPackage();
            package.SetText(p.Value);
            Clipboard.SetContent(package);
            return;
        }

        switch (p.Kind)
        {
            case PayloadKind.Select:
                // The path is one we wrote ourselves, from a sanitized name, so it can't contain a quote.
                Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{p.Value}\"" });
                break;
            case PayloadKind.Folder:
                Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{p.Value}\"" });
                break;
            case PayloadKind.Link:
                // Re-checked at click time: only http and https are ever opened.
                if (LinkDetector.TryGetLink(p.Value, out var uri)) await Windows.System.Launcher.LaunchUriAsync(uri);
                break;
        }
    }

    // ---- incoming transfers

    private sealed class QuietTransfer : IIncomingTransfer
    {
        public CancellationToken Cancelled => CancellationToken.None;
        public void Progress(long bytesReceived) { }
        public void End() { }
    }

    private sealed class ProgressTransfer : IIncomingTransfer
    {
        private readonly Notifier _owner;
        private readonly string _id = NewId();
        private readonly long _total;
        private readonly CancellationTokenSource _cts = new();
        // Starts at 1: AppNotificationProgressData(0) throws "The parameter is incorrect", which
        // failed every transfer over 20 MB with a 500 before a byte was read (found in Verify).
        private uint _sequence = 1;

        public ProgressTransfer(Notifier owner, Identity from, Offer offer)
        {
            _owner = owner;
            _total = offer.ExpectedBytes;
            owner._cancels[_id] = _cts.Cancel;

            var notification = new AppNotificationBuilder()
                .AddText(Notices.Progress(from.Name, offer).Title)
                .AddProgressBar(new AppNotificationProgressBar().BindValue().BindValueStringOverride().BindStatus())
                .AddButton(new AppNotificationButton("Cancel").AddArgument("action", "cancel").AddArgument("id", _id))
                .BuildNotification();
            notification.Tag = _id;
            notification.Group = "progress";
            notification.Progress = new AppNotificationProgressData(_sequence) { Value = 0, ValueStringOverride = "0 B", Status = "Receiving" };
            AppNotificationManager.Default.Show(notification);
        }

        public CancellationToken Cancelled => _cts.Token;

        public void Progress(long bytesReceived)
        {
            var data = new AppNotificationProgressData(Interlocked.Increment(ref _sequence))
            {
                Value = _total == 0 ? 1 : (double)bytesReceived / _total,
                ValueStringOverride = $"{Notices.FormatSize(bytesReceived)} of {Notices.FormatSize(_total)}",
                Status = "Receiving",
            };
            _ = AppNotificationManager.Default.UpdateAsync(data, _id, "progress");
        }

        public void End()
        {
            _owner._cancels.TryRemove(_id, out _);
            _ = AppNotificationManager.Default.RemoveByTagAndGroupAsync(_id, "progress");
        }
    }
}
