using Microsoft.UI.Xaml;

namespace Tailshare.App;

/// <summary>
/// The WinUI application object. App.xaml only merges the stock control styles; every window is built in code.
/// Startup is driven from Program (not OnLaunched): a share-sheet activation must reach AppHost the same way.
/// </summary>
public sealed partial class App : Application
{
    // The styles can't be merged from code: both Resources in the constructor and XamlControlsResources
    // built by hand failed at launch ("catastrophic failure", then a missing AcrylicBackgroundFillColorDefaultBrush).
    internal App() => InitializeComponent();
}
