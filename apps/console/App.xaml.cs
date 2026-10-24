using Microsoft.UI.Xaml;

namespace WorkBuddyRemote.WinUI;

public partial class App : Application
{
    private Window? _window;
    private readonly SingleInstance _single = new();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!_single.TryAcquire())
        {
            // 已有实例：把它的窗口叫到前面，自己直接退出。
            SingleInstance.SignalFirstInstance();
            Exit();
            return;
        }

        var window = new MainWindow();
        _window = window;
        _single.StartListening(() => (window as MainWindow)?.ShowFromTray());
        window.Activate();
    }
}
