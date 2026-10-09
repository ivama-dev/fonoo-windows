using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Fonoo.Windows;

public partial class App : Application
{
    private Window? window;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
#if !FONOO_DESIGN_PREVIEW
        var instance = AppInstance.FindOrRegisterForKey("Fonoo.Windows.Main");
        if (!instance.IsCurrent)
        {
            await instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit(); return;
        }
        instance.Activated += (_, _) => window?.DispatcherQueue.TryEnqueue(() => (window as MainWindow)?.ShowMain());
#endif
        window = new MainWindow();
        window.Activate();
    }
}
