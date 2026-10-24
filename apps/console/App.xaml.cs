using Microsoft.UI.Xaml;

namespace WorkBuddyRemote.WinUI;

public partial class App : Application
{
    private Window? _window;
    private readonly SingleInstance _single = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>XAML 层的未处理异常直接写文件，避免"一闪而过"没有任何线索。</summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkBuddyRemote");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Message}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
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
