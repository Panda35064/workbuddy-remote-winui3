using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace WorkBuddyRemote.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly string _projectRoot;
    private AppSettings _settings;
    private readonly bool _autostart;
    private readonly DispatcherTimer _timer = new();
    private readonly BridgeHost _bridge = new();
    private TrayIcon? _tray;
    private bool _busy;
    private bool _initialized;
    private bool _exiting;
    private NavigationViewItem? _sectionItem;
    private bool _settingsSection;
    private bool _hadPassword;
    private readonly StringBuilder _log = new();

    public MainWindow()
    {
        InitializeComponent();

        _projectRoot = ProjectLocator.Resolve() ?? PromptForProjectRoot();
        _settings = AppSettings.Load(_projectRoot);
        _autostart = Environment.GetCommandLineArgs()
            .Any(arg => string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase));

        Title = "WorkBuddy Remote 控制台";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            try
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
            }
            catch
            {
                // 系统不支持时退回纯色背景
            }
        }

        var appWindow = AppWindow;
        appWindow.Resize(new SizeInt32(1040, 780));
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(icon))
        {
            appWindow.SetIcon(icon);
        }
        if (appWindow.TitleBar is { } titleBar)
        {
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }

        _sectionItem = Nav.MenuItems[0] as NavigationViewItem;
        Nav.SelectedItem = _sectionItem;

        _bridge.Log += line => Dispatch(() => AppendLog("  " + line));
        _bridge.StateChanged += () => Dispatch(async () => await RefreshStatusAsync());

        appWindow.Closing += OnAppWindowClosing;

        _timer.Interval = TimeSpan.FromSeconds(2.5);
        _timer.Tick += async (_, _) => await RefreshStatusAsync();

        RootGrid.Loaded += OnRootLoaded;
    }

    private void Dispatch(Action action)
    {
        try
        {
            DispatcherQueue.TryEnqueue(() => action());
        }
        catch
        {
        }
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        _sectionItem = Nav.MenuItems[0] as NavigationViewItem;
        Nav.SelectedItem = _sectionItem;
        ShowSection("overview");

        LoadSettingsIntoForm();
        UpdateEnvironmentText();
        PopulateAbout();
        CreateTray();

        AppendLog($"控制台已就绪，项目目录：{_projectRoot}");
        AppendLog($"桥接端口 {_settings.BridgePort} · CDP 端口 {_settings.CdpPort} · 监听 {_settings.ListenHost}");
        AppendLog($"配置文件：{_settings.ConfigPath}");

        await RefreshStatusAsync();
        _timer.Start();

        if (_autostart)
        {
            // 开机静默启动：先尝试接管已有桥接，没有再自己起。
            if (!await NetProbe.HttpOkAsync(_settings.HealthUrl))
            {
                await RunExclusiveAsync("启动中", () => StartCoreAsync(openBrowser: false));
            }

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Minimize();
            }
        }
        else if (AppWindow.Presenter is OverlappedPresenter overlapped
                 && overlapped.State == OverlappedPresenterState.Minimized)
        {
            // 从资源管理器 / 脚本启动时可能继承“最小化”状态，手动启动应当直接显示窗口。
            overlapped.Restore();
            Activate();
        }
    }

    // ---------- 托盘 / 关闭语义 ----------

    private void CreateTray()
    {
        if (_tray is not null)
        {
            return;
        }

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            _tray = new TrayIcon(hwnd, icon, "WorkBuddy Remote 控制台");
            _tray.DoubleClick += () => Dispatch(ShowFromTray);
            _tray.RightClick += point => Dispatch(() => ShowTrayMenu(point));
            AppendLog(_tray.IsAdded ? "托盘图标已创建。" : "托盘图标创建失败（Shell_NotifyIcon 返回 false）。");
        }
        catch (Exception ex)
        {
            AppendLog("托盘图标初始化失败：" + ex.Message);
        }
    }

    private void ShowTrayMenu(TrayIcon.POINT point)
    {
        if (_tray is null)
        {
            return;
        }

        var menu = new MenuFlyout();
        var openItem = new MenuFlyoutItem { Text = "打开控制台", Icon = new FontIcon { Glyph = "\uE8A7" } };
        openItem.Click += (_, _) => ShowFromTray();
        menu.Items.Add(openItem);

        var webItem = new MenuFlyoutItem { Text = "打开网页", Icon = new FontIcon { Glyph = "\uE774" } };
        webItem.Click += (_, _) => OpenUrl();
        menu.Items.Add(webItem);
        menu.Items.Add(new MenuFlyoutSeparator());

        if (_bridge.IsRunning)
        {
            var stopItem = new MenuFlyoutItem { Text = "停止桥接", Icon = new FontIcon { Glyph = "\uE71A" } };
            stopItem.Click += async (_, _) => await RunExclusiveAsync("停止中", StopCoreAsync);
            menu.Items.Add(stopItem);
        }
        else
        {
            var startItem = new MenuFlyoutItem { Text = "启动桥接", Icon = new FontIcon { Glyph = "\uE768" } };
            startItem.Click += async (_, _) => await RunExclusiveAsync("启动中", () => StartCoreAsync(openBrowser: false));
            menu.Items.Add(startItem);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var exitItem = new MenuFlyoutItem { Text = "退出", Icon = new FontIcon { Glyph = "\uE7E8" } };
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(exitItem);

        try
        {
            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            var position = AppWindow.Position;
            var x = Math.Max(0, (point.X - position.X) / scale);
            var y = Math.Max(0, (point.Y - position.Y) / scale);
            menu.ShowAt(RootGrid, new FlyoutShowOptions { Position = new Windows.Foundation.Point(x, y) });
        }
        catch
        {
        }
    }

    public void ShowFromTray()
    {
        // 托盘回调在 UI 线程，命名管道回调在线程池：统一抛回 UI 线程处理。
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ShowFromTray);
            return;
        }

        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            AppWindow.Show();
            Activate();
            SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        catch
        {
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting || !_settings.CloseToTray || _tray is null)
        {
            return;
        }

        args.Cancel = true;
        AppWindow.Hide();

        if (!_settings.TrayTipShown)
        {
            _tray.ShowBalloon("WorkBuddy Remote 仍在运行", "双击托盘图标可以重新打开控制台。右键菜单里可以退出。");
            _settings.TrayTipShown = true;
            try
            {
                _settings.Save();
            }
            catch
            {
            }
        }
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _timer.Stop();

        try
        {
            _bridge.DetachForExit(keepRunning: _settings.KeepBridgeOnExit);
        }
        catch
        {
        }

        _tray?.Dispose();
        _tray = null;

        try
        {
            Close();
        }
        catch
        {
        }

        Application.Current.Exit();
    }

    // ---------- 导航 ----------

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            _sectionItem = null;
            _settingsSection = true;
            ShowSection("settings");
            return;
        }

        if (args.InvokedItemContainer is not NavigationViewItem item)
        {
            return;
        }

        var tag = item.Tag as string ?? string.Empty;
        if (tag == "open-url")
        {
            OpenUrl();
            sender.SelectedItem = _settingsSection ? null : _sectionItem;
            return;
        }

        _sectionItem = item;
        _settingsSection = false;
        ShowSection(tag);
    }

    private void ShowSection(string tag)
    {
        OverviewPanel.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        LogsPanel.Visibility = tag == "logs" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "about")
        {
            PopulateAbout();
        }
    }

    private void OnShowLogsClick(object sender, RoutedEventArgs e)
    {
        var logsItem = Nav.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(item => (item.Tag as string) == "logs");
        if (logsItem is not null)
        {
            _sectionItem = logsItem;
            _settingsSection = false;
            Nav.SelectedItem = logsItem;
            ShowSection("logs");
        }
    }

    // ---------- 状态 ----------

    private async Task RefreshStatusAsync()
    {
        if (_busy)
        {
            return;
        }

        var portOpen = await NetProbe.PortOpenAsync(_settings.BridgePort);
        if (!portOpen)
        {
            SetStatus(InfoBarSeverity.Error, "已停止", "桥接服务未运行，点“启动”开始。");
        }
        else if (await NetProbe.HttpOkAsync(_settings.HealthUrl))
        {
            var ready = await NetProbe.HttpOkAsync(_settings.ReadyUrl);
            SetStatus(InfoBarSeverity.Success, "运行中",
                ready
                    ? $"网页控制台：{_settings.LocalUrl}"
                    : $"端口 {_settings.BridgePort} 已监听，正在等待 WorkBuddy 连接…");
        }
        else
        {
            SetStatus(InfoBarSeverity.Warning, "启动中", "端口已监听，正在等待桥接就绪…");
        }

        WorkDaddyText.Text = await DetectWorkDaddyAsync();
        _tray?.UpdateTooltip(portOpen ? $"WorkBuddy Remote · 运行中（:{_settings.BridgePort}）" : "WorkBuddy Remote · 已停止");
    }

    private static async Task<string> DetectWorkDaddyAsync()
    {
        foreach (var port in new[] { 47832, 47833, 47834, 47835 })
        {
            if (await NetProbe.PortOpenAsync(port))
            {
                return $"运行中（:{port}）";
            }
        }

        return "未运行";
    }

    private void SetStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
    }

    private void SetBusy(string? text)
    {
        _busy = text is not null;
        BusyBar.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        if (_busy && text is not null)
        {
            SetStatus(InfoBarSeverity.Informational, text, "正在执行，请稍候…");
        }

        var enabled = !_busy;
        foreach (var button in new[]
                 {
                     StartButton, RestartBridgeButton, StopButton, OpenButton,
                     RestartAppButton, LogFolderButton, CopyAddressButton,
                 })
        {
            button.IsEnabled = enabled;
        }
    }

    private async Task RunExclusiveAsync(string busyText, Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(busyText);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppendLog("操作失败：" + ex.Message);
        }
        finally
        {
            SetBusy(null);
            await RefreshStatusAsync();
        }
    }

    private void AppendLog(string line)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {line}";
        _log.AppendLine(entry);
        if (_log.Length > 200_000)
        {
            _log.Remove(0, _log.Length - 150_000);
        }

        var text = _log.ToString();
        LogBox.Text = text;
        LogBox.SelectionStart = text.Length;

        var recent = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(6)
            .Select(line => line.TrimEnd('\r'));
        RecentLogText.Text = string.Join('\n', recent);
    }

    private void UpdateEnvironmentText()
    {
        AddressText.Text = _settings.LocalUrl;
        ListenText.Text = _settings.IsLan ? "局域网" : "仅本机";
        PortText.Text = _settings.BridgePort.ToString();
        VersionText.Text = WorkBuddyLocator.DescribeInstalledVersion(_settings.WorkBuddyExePath);
        CdpText.Text = _settings.CdpPort.ToString();
        LogPathText.Text = _settings.RuntimeTempDir;
    }

    private void PopulateAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutVersionText.Text = $"版本 {version?.ToString(3) ?? "2.0.0"} · .NET {Environment.Version.Major} · Windows App SDK";
        AboutProjectText.Text = _projectRoot;
        AboutConfigText.Text = _settings.ConfigPath + (_settings.IsPortable ? "（便携版）" : "");
        AboutLogsText.Text = _settings.RuntimeTempDir;
        AboutPortsText.Text = $"桥接 {_settings.BridgePort} · CDP {_settings.CdpPort}";
    }

    // ---------- 命令 ----------

    private async void OnStartClick(object sender, RoutedEventArgs e)
        => await RunExclusiveAsync("启动中", () => StartCoreAsync(openBrowser: true));

    private async void OnRestartBridgeClick(object sender, RoutedEventArgs e)
        => await RunExclusiveAsync("重启中", () => StartCoreAsync(openBrowser: true, restartWorkBuddy: true));

    private async void OnStopClick(object sender, RoutedEventArgs e)
        => await RunExclusiveAsync("停止中", StopCoreAsync);

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenUrl();

    private void OpenUrl()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_settings.LocalUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("打开页面失败：" + ex.Message);
        }
    }

    private void OnCopyAddressClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(_settings.LocalUrl);
            Clipboard.SetContent(package);
            AppendLog("已复制访问地址：" + _settings.LocalUrl);
        }
        catch (Exception ex)
        {
            AppendLog("复制失败：" + ex.Message);
        }
    }

    private void OnCopyLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(_log.ToString());
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            AppendLog("复制日志失败：" + ex.Message);
        }
    }

    private void OnOpenProjectClick(object sender, RoutedEventArgs e) => OpenFolder(_projectRoot);

    private void OnOpenConfigClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(_settings.ConfigPath))
            {
                _settings.Save();
            }
            Process.Start(new ProcessStartInfo(_settings.ConfigPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("打开配置文件失败：" + ex.Message);
        }
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) => OpenFolder(_settings.RuntimeTempDir);

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("打开目录失败：" + ex.Message);
        }
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        LogBox.Text = string.Empty;
        RecentLogText.Text = "—";
    }

    private async void OnRestartWorkBuddyClick(object sender, RoutedEventArgs e)
        => await RunExclusiveAsync("重启中", () => StartCoreAsync(openBrowser: true, restartWorkBuddy: true));

    // ---------- 设置 ----------

    private void LoadSettingsIntoForm()
    {
        _hadPassword = _settings.HasPassword;
        ScopeBox.SelectedIndex = _settings.IsLan ? 1 : 0;
        BridgePortBox.Value = _settings.BridgePort;
        CdpPortBox.Value = _settings.CdpPort;
        ExePathBox.Text = _settings.WorkBuddyExePath;
        UserDataBox.Text = _settings.WorkBuddyUserDataDir;
        HideWindowCheck.IsOn = _settings.HideWorkBuddyWindowAfterStart;
        KillBeforeStartCheck.IsOn = _settings.KillWorkBuddyProcessesBeforeStart;
        AutoRestartCheck.IsOn = _settings.AutoRestartWorkBuddy;
        StartWorkDaddyCheck.IsOn = _settings.StartWorkDaddy;
        AutoRunCheck.IsOn = StartupShortcut.IsEnabled();
        CloseToTrayCheck.IsOn = _settings.CloseToTray;
        KeepBridgeOnExitCheck.IsOn = _settings.KeepBridgeOnExit;
        FileManagerCheck.IsOn = _settings.EnableFileManager;
        EnableRestartCheck.IsOn = _settings.EnableRestart;
        MaskSecretsCheck.IsOn = _settings.MaskBridgeModelSecrets;
        ConfigHint.Text = "配置文件：" + _settings.ConfigPath;
        PasswordBox1.PlaceholderText = _hadPassword ? "留空表示不修改" : "未设置";
        PasswordBox2.PlaceholderText = "确认密码";
        PasswordBox1.Password = string.Empty;
        PasswordBox2.Password = string.Empty;
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PasswordBox1 is null)
        {
            return;
        }

        PasswordBox1.PlaceholderText = ScopeBox.SelectedIndex == 1
            ? "局域网访问需要密码"
            : (_hadPassword ? "留空表示不修改" : "未设置");
    }

    private void OnReloadSettingsClick(object sender, RoutedEventArgs e)
    {
        _settings = AppSettings.Load(_projectRoot);
        LoadSettingsIntoForm();
        ShowSettingsInfo(InfoBarSeverity.Informational, "已重新载入", "表单已与磁盘上的配置同步。");
    }

    private void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsInfoBar.IsOpen = false;

        var bridgePort = ReadPort(BridgePortBox.Value);
        if (bridgePort is null)
        {
            ShowSettingsError("桥接端口必须是 1-65535 的整数。");
            return;
        }

        var cdpPort = ReadPort(CdpPortBox.Value);
        if (cdpPort is null)
        {
            ShowSettingsError("CDP 端口必须是 1-65535 的整数。");
            return;
        }

        if (bridgePort == cdpPort)
        {
            ShowSettingsError("桥接端口和 CDP 端口不能相同。");
            return;
        }

        var password = PasswordBox1.Password;
        var confirm = PasswordBox2.Password;
        if (!string.IsNullOrEmpty(password) || !string.IsNullOrEmpty(confirm))
        {
            if (!string.Equals(password, confirm, StringComparison.Ordinal))
            {
                ShowSettingsError("两次输入的密码不一致。");
                return;
            }
            if (password.Length < 4)
            {
                ShowSettingsError("密码至少 4 位。");
                return;
            }
        }

        var isLan = ScopeBox.SelectedIndex == 1;
        if (isLan && string.IsNullOrEmpty(password) && !_hadPassword)
        {
            ShowSettingsError("局域网访问必须设置访问密码。");
            return;
        }

        _settings.BridgePort = bridgePort.Value;
        _settings.CdpPort = cdpPort.Value;
        _settings.ListenHost = isLan ? "0.0.0.0" : "127.0.0.1";
        _settings.WorkBuddyExePath = ExePathBox.Text.Trim();
        _settings.WorkBuddyUserDataDir = UserDataBox.Text.Trim();
        _settings.HideWorkBuddyWindowAfterStart = HideWindowCheck.IsOn;
        _settings.KillWorkBuddyProcessesBeforeStart = KillBeforeStartCheck.IsOn;
        _settings.AutoRestartWorkBuddy = AutoRestartCheck.IsOn;
        _settings.StartWorkDaddy = StartWorkDaddyCheck.IsOn;
        _settings.CloseToTray = CloseToTrayCheck.IsOn;
        _settings.KeepBridgeOnExit = KeepBridgeOnExitCheck.IsOn;
        _settings.EnableFileManager = FileManagerCheck.IsOn;
        _settings.EnableRestart = EnableRestartCheck.IsOn;
        _settings.MaskBridgeModelSecrets = MaskSecretsCheck.IsOn;

        if (!string.IsNullOrEmpty(password))
        {
            _settings.PasswordHash = Passwords.CreateHash(password);
        }

        try
        {
            StartupShortcut.Apply(AutoRunCheck.IsOn);
        }
        catch (Exception ex)
        {
            ShowSettingsError("设置开机启动失败：" + ex.Message);
            return;
        }

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            ShowSettingsError("写入配置文件失败：" + ex.Message);
            return;
        }

        _hadPassword = _settings.HasPassword;
        PasswordBox1.Password = string.Empty;
        PasswordBox2.Password = string.Empty;
        UpdateEnvironmentText();
        PopulateAbout();
        AppendLog("设置已保存。");
        ShowSettingsInfo(InfoBarSeverity.Success, "已保存", "重新启动桥接后生效。");
    }

    private static int? ReadPort(double value)
    {
        if (double.IsNaN(value))
        {
            return null;
        }

        var port = (int)Math.Round(value);
        return port is >= 1 and <= 65535 ? port : null;
    }

    private void ShowSettingsError(string message)
    {
        ShowSettingsInfo(InfoBarSeverity.Error, "无法保存", message);
    }

    private void ShowSettingsInfo(InfoBarSeverity severity, string title, string message)
    {
        SettingsInfoBar.Severity = severity;
        SettingsInfoBar.Title = title;
        SettingsInfoBar.Message = message;
        SettingsInfoBar.IsOpen = true;
    }

    // ---------- 启动 / 停止 ----------

    private async Task StartCoreAsync(bool openBrowser, bool restartWorkBuddy = false)
    {
        AppendLog(restartWorkBuddy ? "正在重启 WorkBuddy 并启动桥接…" : "正在启动桥接服务…");

        var result = await _bridge.StartAsync(_settings, restartWorkBuddy);
        if (!result.Ok && result.NeedsWorkBuddyRestart)
        {
            if (_autostart && _settings.AutoRestartWorkBuddy)
            {
                AppendLog("开机静默启动：自动重启 WorkBuddy 以开启调试端口…");
                result = await _bridge.StartAsync(_settings, restartWorkBuddy: true);
            }
            else if (!_autostart)
            {
                AppendLog("WorkBuddy 需要重启一次才能接上桥接，正在自动重启…");
                result = await _bridge.StartAsync(_settings, restartWorkBuddy: true);
            }
        }

        AppendLog(result.Message);
        UpdateEnvironmentText();

        if (result.Ok && openBrowser)
        {
            OpenUrl();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_bridge.IsExternal)
        {
            AppendLog("当前桥接不是这个控制台启动的（可能是上一次保留下来的），无法在此停止。");
            return;
        }

        AppendLog("正在停止桥接服务…");
        await _bridge.StopAsync();
        AppendLog("桥接服务已停止。");
    }

    private static string PromptForProjectRoot()
    {
        var guess = @"D:\Program Files\Tools\workbuddy-remote-v2";
        if (Directory.Exists(guess))
        {
            return guess;
        }

        return AppContext.BaseDirectory;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
