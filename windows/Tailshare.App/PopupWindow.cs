using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tailshare.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Tailshare.App;

/// <summary>The tray popup: pick a device, pick files or type text, send.</summary>
internal sealed class PopupWindow : Window
{
    private readonly AppHost _host;
    private readonly SendController _send;
    private readonly DevicePanel _devices;
    private readonly RadioButtons _mode = new() { MaxColumns = 2 };
    private readonly StackPanel _filesPanel = new() { Spacing = 8 };
    private readonly StackPanel _pickedList = new() { Spacing = 4 };
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140, Visibility = Visibility.Collapsed };
    private readonly Button _sendButton = new() { Content = "Send", IsEnabled = false };
    private readonly Button _cancelButton = new() { Content = "Cancel", Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Visibility = Visibility.Collapsed };
    private readonly List<string> _picked = [];
    private bool _allowClose;

    public PopupWindow(AppHost host)
    {
        _host = host;
        _send = new SendController(host);
        _devices = new DevicePanel(host);
        Title = "tailshare";

        _mode.Items.Add("Files");
        _mode.Items.Add("Text");
        _mode.SelectedIndex = 0;

        var browse = new Button { Content = "Browse" };
        var folder = new Button { Content = "Folder" };
        var pickRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { browse, folder } };
        _filesPanel.Children.Add(pickRow);
        _filesPanel.Children.Add(_pickedList);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _sendButton, _cancelButton } };
        var content = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(16),
            Children = { _devices, _mode, _filesPanel, _text, buttons, _status, _progress },
        };

        // A transparent background is what lets an empty area receive a drop.
        var root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), AllowDrop = true };
        root.Children.Add(new ScrollViewer { Content = content });
        root.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
        };
        root.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            _mode.SelectedIndex = 0;
            AddPicked(items.Select(i => i.Path));
        };
        Content = root;

        browse.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHelper.Hwnd(this));
            var files = await picker.PickMultipleFilesAsync();
            if (files is not null) AddPicked(files.Select(f => f.Path));
        };
        folder.Click += async (_, _) =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHelper.Hwnd(this));
            var dir = await picker.PickSingleFolderAsync();
            if (dir is not null) AddPicked([dir.Path]);
        };

        _mode.SelectionChanged += (_, _) =>
        {
            var files = _mode.SelectedIndex == 0;
            _filesPanel.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
            _text.Visibility = files ? Visibility.Collapsed : Visibility.Visible;
            UpdateSendEnabled();
        };
        _text.TextChanged += (_, _) => UpdateSendEnabled();
        _devices.SelectionChanged += UpdateSendEnabled;

        _sendButton.Click += async (_, _) => await SendAsync();
        _cancelButton.Click += (_, _) => _send.Cancel();
        _send.Changed += OnStatus;

        WindowHelper.Configure(this, 440, 700, bottomRight: true);

        // The X hides the popup; Quit in the tray menu is the only way out.
        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            AppWindow.Hide();
        };
    }

    public void AllowClose() => _allowClose = true;

    public void ShowAndRefresh()
    {
        WindowHelper.BringToFront(this);
        _ = _devices.RefreshAsync();
    }

    private void AddPicked(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrEmpty(path) && !_picked.Contains(path)) _picked.Add(path);
        }
        RebuildPicked();
    }

    private void RebuildPicked()
    {
        _pickedList.Children.Clear();
        foreach (var path in _picked.ToList())
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock
            {
                Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => { _picked.Remove(path); RebuildPicked(); };
            Grid.SetColumn(remove, 1);
            row.Children.Add(name);
            row.Children.Add(remove);
            _pickedList.Children.Add(row);
        }
        UpdateSendEnabled();
    }

    private SendContent? CurrentContent()
    {
        if (_mode.SelectedIndex == 0) return _picked.Count > 0 ? new SendContent.Paths(_picked.ToList()) : null;
        return _text.Text.Length > 0 ? new SendContent.Text(_text.Text) : null;
    }

    private void UpdateSendEnabled() =>
        _sendButton.IsEnabled = !_send.Busy && _devices.Selected is not null && CurrentContent() is not null;

    private async Task SendAsync()
    {
        var to = _devices.Selected;
        var content = CurrentContent();
        if (to is null || content is null) return;
        await _send.SendAsync(to, content);
        UpdateSendEnabled();
    }

    private void OnStatus(SendStatus s)
    {
        _status.Text = s.Text;
        _progress.Visibility = s.Busy || s.Fraction is not null ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = s.Busy && s.Fraction is null;
        if (s.Fraction is { } f) _progress.Value = f * 100;
        _cancelButton.Visibility = s.Busy ? Visibility.Visible : Visibility.Collapsed;
        _sendButton.IsEnabled = !s.Busy && _devices.Selected is not null && CurrentContent() is not null;
    }
}
