using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tailshare.Core;

namespace Tailshare.App;

/// <summary>The device list with its Refresh button, shared by the tray popup and the share window.</summary>
internal sealed class DevicePanel : StackPanel
{
    private sealed record DeviceItem(PeerInfo Peer)
    {
        // ListView shows ToString() for an item with no template.
        public override string ToString() => Peer.Identity.Name;
    }

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

    public PeerInfo? Selected => (_list.SelectedItem as DeviceItem)?.Peer;

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        _refresh.IsEnabled = false;
        var keep = Selected?.Identity.Id;
        try
        {
            var status = await _host.PollOnceAsync();
            IReadOnlyList<PeerInfo> found = [];
            if (status.Connected)
            {
                using var sender = new Sender(_host.Port);
                found = await sender.DiscoverAsync(status, CancellationToken.None);
            }

            _list.Items.Clear();
            foreach (var peer in found) _list.Items.Add(new DeviceItem(peer));
            _list.SelectedItem = _list.Items.OfType<DeviceItem>().FirstOrDefault(d => d.Peer.Identity.Id == keep);

            _state.Text = !status.Connected ? "Tailscale not connected" : found.Count == 0 ? "No devices" : "";
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
