using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tailshare.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;

namespace Tailshare.App;

/// <summary>The small window the Windows share sheet opens: pick a device and Send. It closes when the send ends.</summary>
internal sealed class ShareWindow : Window
{
    private readonly AppHost _host;
    private readonly ShareOperation _operation;
    private readonly SendController _send;
    private readonly DevicePanel _devices;
    private readonly TextBlock _what = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _sendButton = new() { Content = "Send", IsEnabled = false };
    private readonly Button _cancelButton = new() { Content = "Cancel", Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Visibility = Visibility.Collapsed };
    private SendContent? _content;
    private bool _reported;
    private bool _sent;
    private bool _closed;

    public ShareWindow(AppHost host, ShareOperation operation)
    {
        _host = host;
        _operation = operation;
        _send = new SendController(host);
        _devices = new DevicePanel(host);
        Title = "tailshare";

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _sendButton, _cancelButton } };
        Content = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(16),
            Children = { _what, _devices, buttons, _status, _progress },
        };

        _devices.SelectionChanged += UpdateSendEnabled;
        _sendButton.Click += async (_, _) => await SendAsync();
        _cancelButton.Click += (_, _) => _send.Cancel();
        _send.Changed += OnStatus;
        Closed += (_, _) => { _closed = true; ReportCompleted(); };

        // On top: the share sheet hands over to this window and it must not fall behind the app shared from.
        WindowHelper.Configure(this, 400, 420, bottomRight: false, alwaysOnTop: true);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _content = await ReadContentAsync();
        _what.Text = _content switch
        {
            SendContent.Paths p => DescribePaths(p.Items),
            SendContent.Text t => "Text: " + (t.Value.Length > 80 ? t.Value[..80] + "…" : t.Value),
            _ => "Nothing to send",
        };
        UpdateSendEnabled();
        await _devices.RefreshAsync();
    }

    // Files first, then a web link, then plain text: a browser shares both a link and its title.
    private async Task<SendContent?> ReadContentAsync()
    {
        try
        {
            var data = _operation.Data;
            if (data.Contains(StandardDataFormats.StorageItems))
            {
                var items = await data.GetStorageItemsAsync();
                var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                if (paths.Count > 0) return new SendContent.Paths(paths);
            }
            if (data.Contains(StandardDataFormats.WebLink)) return new SendContent.Text((await data.GetWebLinkAsync()).AbsoluteUri);
            if (data.Contains(StandardDataFormats.Text)) return new SendContent.Text(await data.GetTextAsync());
            return null;
        }
        finally
        {
            try { _operation.ReportDataRetrieved(); } catch (Exception) { }
        }
    }

    private static string DescribePaths(IReadOnlyList<string> paths)
    {
        var first = Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0]));
        return paths.Count == 1 ? first : $"{first} and {paths.Count - 1} more";
    }

    private void UpdateSendEnabled() =>
        _sendButton.IsEnabled = !_sent && !_send.Busy && _devices.Selected is not null && _content is not null;

    private async Task SendAsync()
    {
        var to = _devices.Selected;
        if (to is null || _content is null) return;

        // One send per share window, even while a hold for a phone is only waiting.
        _sent = true;
        UpdateSendEnabled();
        if (to.Computer is { } computer)
        {
            var outcome = await _send.SendAsync(computer, _content);
            // The result is a notification, and the window is done.
            _host.Notifier.Show("tailshare", SendController.ShareResultText(outcome, to.Identity.Name));
        }
        else
        {
            // The controller raises the hold's notification itself: this window may be gone before it ends.
            await _send.HoldAsync(to.Identity, _content);
        }
        if (!_closed) Close();
    }

    private void OnStatus(SendStatus s)
    {
        _status.Text = s.Text;
        _progress.Visibility = s.Busy || s.Fraction is not null ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = s.Busy && s.Fraction is null;
        if (s.Fraction is { } f) _progress.Value = f * 100;
        _cancelButton.Visibility = s.Cancellable ? Visibility.Visible : Visibility.Collapsed;
        UpdateSendEnabled();
    }

    private void ReportCompleted()
    {
        if (_reported) return;
        _reported = true;
        try { _operation.ReportCompleted(); } catch (Exception) { }
    }
}
