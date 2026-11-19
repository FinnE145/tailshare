using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tailshare.Core;

namespace Tailshare.App;

/// <summary>The device list with its Refresh button, shared by the tray popup and the share window.</summary>
internal sealed class DevicePanel : StackPanel
{
    // Segoe Fluent Icons "CellPhone".
    private const string PhoneGlyph = "";

    private readonly AppHost _host;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 150 };
    private readonly TextBlock _state = new() { Visibility = Visibility.Collapsed };
    private readonly Button _refresh = new() { Content = "Refresh" };
    private bool _refreshing;

    public event Action? SelectionChanged;

    public DevicePanel(AppHost host)
    {
        _host = host;
        Spacing = 8;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = "Devices", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_refresh, 1);
        header.Children.Add(title);
        header.Children.Add(_refresh);

        Children.Add(header);
        Children.Add(_list);
        Children.Add(_state);

        _list.SelectionChanged += (_, _) => SelectionChanged?.Invoke();
        _refresh.Click += async (_, _) => await RefreshAsync();
    }

    public Target? Selected => (_list.SelectedItem as ListViewItem)?.Tag as Target;

    // A phone or iPad carries an icon beside its name: the row is otherwise the same as a computer's.
    private static ListViewItem MakeItem(Target target)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (target.IsMobile) row.Children.Add(new FontIcon { Glyph = PhoneGlyph, FontFamily = new FontFamily("Segoe Fluent Icons") });
        row.Children.Add(new TextBlock { Text = target.Identity.Name });
        return new ListViewItem { Content = row, Tag = target };
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        _refresh.IsEnabled = false;
        var keep = Selected?.Identity.Id;
        try
        {
            var status = await _host.PollOnceAsync();
            var found = new DeviceList([], []);
            if (status.Connected)
            {
                using var sender = new Sender(_host.Port);
                // Read fresh, so a hand edit of the hub setting applies at the next refresh.
                found = await sender.DiscoverDevicesAsync(status, _host.Settings.Load().Value.EffectiveHub, CancellationToken.None);
            }

            var targets = found.Computers.Select(c => new Target(c.Identity, c))
                .Concat(found.Mobiles.Select(m => new Target(m, null)))
                .ToList();

            _list.Items.Clear();
            foreach (var target in targets) _list.Items.Add(MakeItem(target));
            _list.SelectedItem = _list.Items.OfType<ListViewItem>().FirstOrDefault(i => ((Target)i.Tag).Identity.Id == keep);

            _state.Text = !status.Connected ? "Tailscale not connected" : targets.Count == 0 ? "No devices" : "";
            _state.Visibility = _state.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _refreshing = false;
            _refresh.IsEnabled = true;
            SelectionChanged?.Invoke();
        }
    }
}
