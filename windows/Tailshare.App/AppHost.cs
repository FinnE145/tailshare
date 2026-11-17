using System.Net;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
using Tailshare.Core;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;

namespace Tailshare.App;

/// <summary>
/// Owns everything long-lived: the tray icon, the receiver, the 10-second Tailscale check, the popup.
/// Core does the protocol; this decides what Windows does around it.
/// </summary>
internal sealed class AppHost
{
    private readonly DispatcherQueue _ui;
    private readonly CliTailscale _tailscale = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    private TaskbarIcon? _tray;
    private PopupWindow? _popup;
    private Receiver? _receiver;
    private IPAddress? _boundIp;
    private int _boundPort;
    private bool _portErrorShown;

    public SettingsStore Settings { get; }
    public Notifier Notifier { get; }
    public ITailscale Tailscale => _tailscale;
    public TailnetStatus Status { get; private set; } = TailnetStatus.Disconnected;

    /// <summary>Read fresh, so a hand edit of settings.json applies at the next 10-second check.</summary>
    public int Port => Settings.Load().Value.EffectivePort;

    public AppHost(DispatcherQueue ui)
    {
        _ui = ui;
        Settings = new SettingsStore(Path.Combine(ApplicationData.Current.LocalFolder.Path, "settings.json"));
        Notifier = new Notifier(ui);
    }

    public void Start(AppActivationArguments? first)
    {
        // Before anything can show a notification. Verify found this was never called: no click,
        // Accept or Decline reached the app, so every first contact would have timed out.
        Notifier.Register();
        CreateTray();
        _ = PollForeverAsync();
        _ = RequestStartupOnceAsync();
        // Launched by the startup task means "run in the tray", not "show a window".
        if (first is null || first.Kind != ExtendedActivationKind.StartupTask) OnActivated(first);
    }

    public void OnActivated(AppActivationArguments? args)
    {
        if (args?.Kind == ExtendedActivationKind.ShareTarget && args.Data is IShareTargetActivatedEventArgs share)
        {
            new ShareWindow(this, share.ShareOperation).Activate();
        }
        else if (args?.Kind is null or ExtendedActivationKind.Launch)
        {
            ShowPopup();
        }
    }

    public void ShowPopup()
    {
        _popup ??= new PopupWindow(this);
        _popup.ShowAndRefresh();
    }

    public void Quit()
    {
        _tray?.Dispose();
        Notifier.Unregister();
        _popup?.AllowClose();
        _receiver?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        Application.Current.Exit();
    }

    private void CreateTray()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "Open", Command = new ActionCommand(ShowPopup) });
        menu.Items.Add(new MenuFlyoutItem { Text = "Quit", Command = new ActionCommand(Quit) });

        _tray = new TaskbarIcon
        {
            ToolTipText = "tailshare",
            IconSource = new GeneratedIconSource
            {
                Text = "T",
                Foreground = new SolidColorBrush(Colors.White),
                Background = new SolidColorBrush(Colors.RoyalBlue),
            },
            ContextFlyout = menu,
            ContextMenuMode = ContextMenuMode.PopupMenu,
            NoLeftClickDelay = true,
            LeftClickCommand = new ActionCommand(ShowPopup),
        };
        // No efficiency mode: Windows would throttle the process while it sits hidden, and it is a server.
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    private void SetTrayState(string? problem) =>
        _tray!.ToolTipText = problem is null ? "tailshare" : "tailshare: " + problem;

    private async Task PollForeverAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do { await PollOnceAsync(); } while (await timer.WaitForNextTickAsync());
    }

    /// <summary>Re-reads Tailscale and brings the receiver in line with it: bound to this device's address and the settings' port, or stopped.</summary>
    public async Task<TailnetStatus> PollOnceAsync()
    {
        await _pollGate.WaitAsync();
        try
        {
            Status = await _tailscale.GetStatusAsync(CancellationToken.None);
            await EnsureReceiverAsync();
            return Status;
        }
        finally { _pollGate.Release(); }
    }

    private async Task EnsureReceiverAsync()
    {
        var port = Port;

        if (!Status.Connected)
        {
            await StopReceiverAsync();
            _portErrorShown = false;
            SetTrayState("Tailscale not connected");
            return;
        }

        if (_receiver is not null && Equals(_boundIp, Status.SelfIp) && _boundPort == port) return;

        await StopReceiverAsync();
        var receiver = new Receiver(new ReceiverOptions
        {
            Settings = Settings,
            Tailscale = _tailscale,
            Host = Notifier,
            DefaultReceiveFolder = UserDataPaths.GetDefault().Downloads,
        });
        try
        {
            await receiver.StartAsync(Status.SelfIp!, port);
            _receiver = receiver;
            _boundIp = Status.SelfIp;
            _boundPort = port;
            _portErrorShown = false;
            SetTrayState(null);
        }
        catch (Exception ex)
        {
            await receiver.DisposeAsync();
            var message = ex is PortInUseException ? $"Can't receive: port {port} is in use" : "Can't receive";
            SetTrayState(message);
            // Retried every 10 seconds, but said once.
            if (!_portErrorShown) Notifier.Show("tailshare", message);
            _portErrorShown = true;
        }
    }

    private async Task StopReceiverAsync()
    {
        if (_receiver is null) return;
        await _receiver.DisposeAsync();
        _receiver = null;
        _boundIp = null;
    }

    // Asked once. The user can turn it off in Windows' Startup apps and we won't ask again.
    private static async Task RequestStartupOnceAsync()
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        if (values.ContainsKey("startupRequested")) return;
        values["startupRequested"] = true;

        var task = await StartupTask.GetAsync("TailshareStartup");
        if (task.State == StartupTaskState.Disabled) await task.RequestEnableAsync();
    }
}

internal sealed class ActionCommand(Action run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}
