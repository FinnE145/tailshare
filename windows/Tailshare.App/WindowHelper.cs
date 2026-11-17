using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Tailshare.App;

internal static class WindowHelper
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public static IntPtr Hwnd(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

    /// <summary>A fixed-size window in device-independent pixels, in the bottom-right corner of the work area when asked.</summary>
    public static void Configure(Window window, int width, int height, bool bottomRight)
    {
        var appWindow = window.AppWindow;
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        appWindow.SetPresenter(presenter);

        var scale = GetDpiForWindow(Hwnd(window)) / 96.0;
        var w = (int)(width * scale);
        var h = (int)(height * scale);
        appWindow.Resize(new SizeInt32(w, h));

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var margin = (int)(12 * scale);
        appWindow.Move(bottomRight
            ? new PointInt32(area.X + area.Width - w - margin, area.Y + area.Height - h - margin)
            : new PointInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));
    }
}
