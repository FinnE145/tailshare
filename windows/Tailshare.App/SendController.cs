using Tailshare.Core;

namespace Tailshare.App;

/// <param name="Fraction">0 to 1, or null when there is nothing to measure (waiting, preparing).</param>
internal sealed record SendStatus(string Text, double? Fraction, bool Busy);

/// <summary>One send at a time for a window: runs the Core sender and turns its progress into status lines.</summary>
internal sealed class SendController(AppHost host)
{
    private CancellationTokenSource? _cts;

    public event Action<SendStatus>? Changed;

    public bool Busy => _cts is not null;

    public async Task<SendOutcome> SendAsync(PeerInfo to, SendContent content)
    {
        var device = to.Identity.Name;
        using var cts = _cts = new CancellationTokenSource();
        Changed?.Invoke(new SendStatus($"Waiting for {device}", null, true));

        // Progress<T> posts to the UI thread, where this controller lives.
        var progress = new Progress<SendProgress>(p => Changed?.Invoke(p.Stage switch
        {
            SendStage.Preparing => new SendStatus("Preparing", null, true),
            SendStage.Waiting => new SendStatus($"Waiting for {device}", null, true),
            _ => new SendStatus(
                $"Sending to {device}: {Notices.FormatSize(p.BytesSent)} of {Notices.FormatSize(p.BytesTotal)}",
                p.BytesTotal == 0 ? 1 : (double)p.BytesSent / p.BytesTotal, true),
        }));

        SendOutcome outcome;
        using (var sender = new Sender(host.Port))
        {
            outcome = await sender.SendAsync(to, content, progress, cts.Token);
        }
        _cts = null;

        Changed?.Invoke(new SendStatus(ResultText(outcome, device), outcome == SendOutcome.Sent ? 1 : null, false));
        return outcome;
    }

    public void Cancel() => _cts?.Cancel();

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
