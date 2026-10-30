using System.Diagnostics;
using System.IO.Compression;
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

        ShowFromTrayCommand = new RelayCommand(ShowFromTray);

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

        _ = CheckForUpdatesAsync();

        if (_autostart)
        {
            // 开机静默启动：WorkBuddy 是 GUI 程序，CreateNoWindow 对它无效，
            // 需要让桥接连上后主动把它的窗口收起来（只在内存里改，不写回配置）。
            _settings.HideWorkBuddyWindowAfterStart = true;

            // 先尝试接管已有桥接，没有再自己起。
            if (!await NetProbe.HttpOkAsync(_settings.HealthUrl))
            {
                await RunExclusiveAsync("启动中", () => StartCoreAsync(openBrowser: false));
            }

            // 开机静默启动：不显示窗口、不弹任何提示，只在托盘留一个图标。
            AppWindow.Hide();
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
        try
        {
            // H.NotifyIcon 的 TaskbarIcon 在 XAML 里声明，这里只负责确保系统托盘图标注册成功。
            TrayIconHost.ForceCreate();
            AppendLog("托盘图标已创建（H.NotifyIcon）。");
        }
        catch (Exception ex)
        {
            AppendLog("托盘图标初始化失败：" + ex.Message);
        }
    }

    // ---------- 托盘菜单（菜单项在 XAML 里声明，这里只处理命令） ----------
    // 托盘双击唤回控制台（H.NotifyIcon 用 ICommand，不暴露 Click 事件）。
    public System.Windows.Input.ICommand ShowFromTrayCommand { get; }

    // H.NotifyIcon 的 SecondWindow 模式会在显示前按自己的位置计算覆盖 Placement，
    // 导致菜单从上方往下展开。这里在打开事件里最后纠正一次方向。
    private void OnTrayMenuOpening(object? sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            flyout.Placement = FlyoutPlacementMode.Top;
        }
    }

    private void OnTrayOpenClick(object sender, RoutedEventArgs e) => ShowFromTray();

    private void OnTrayOpenWebClick(object sender, RoutedEventArgs e) => OpenUrl();

    private async void OnTrayToggleBridgeClick(object sender, RoutedEventArgs e)
    {
        if (_bridge.IsRunning)
        {
            await RunExclusiveAsync("停止中", StopCoreAsync);
        }
        else
        {
            await RunExclusiveAsync("启动中", () => StartCoreAsync(openBrowser: false));
        }
    }

    private async void OnTrayRestartBridgeClick(object sender, RoutedEventArgs e)
        => await RunExclusiveAsync("重启中", RestartBridgeAsync);

    private void OnTrayExitClick(object sender, RoutedEventArgs e) => ExitApplication();

    private void OnDetectWorkDaddyClick(object sender, RoutedEventArgs e)
    {
        var found = FindWorkDaddyExe(WorkDaddyPathBox.Text);
        if (found is null)
        {
            WorkDaddyStatusText.Text = "未找到 WorkDaddy，请手动指定程序路径";
            return;
        }

        WorkDaddyPathBox.Text = found;
        WorkDaddyStatusText.Text = "已找到：" + Path.GetFileName(found);
        AppendLog("检测到 WorkDaddy：" + found);
    }

    private static string? FindWorkDaddyExe(string? preferred)
    {
        var trimmed = (preferred ?? "").Trim();
        if (trimmed.Length > 0 && File.Exists(trimmed))
        {
            return trimmed;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "WorkDaddy", "WorkDaddy.exe"),
            Path.Combine(localAppData, "WorkDaddy", "WorkDaddy.exe"),
            Path.Combine(programFiles, "WorkDaddy", "WorkDaddy.exe"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>桥接就绪后按需拉起 WorkDaddy；CDP 端口必须与桥接一致，否则它注入会失效。</summary>
    private void StartWorkDaddyIfNeeded()
    {
        try
        {
            var exe = FindWorkDaddyExe(_settings.WorkDaddyLauncherPath);
            if (exe is null)
            {
                AppendLog("已开启 WorkDaddy，但没找到程序路径，请在设置里指定或点一次「检测插件」。");
                return;
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            psi.Environment["WBSWITCH_CDP_PORT"] = _settings.CdpPort.ToString();
            Process.Start(psi);
            AppendLog("已拉起 WorkDaddy（CDP 端口 " + _settings.CdpPort + "）。");
        }
        catch (Exception ex)
        {
            AppendLog("拉起 WorkDaddy 失败：" + ex.Message);
        }
    }

    private async Task RestartBridgeAsync()
    {
        if (!_bridge.IsExternal)
        {
            await StopCoreAsync();
        }
        await StartCoreAsync(openBrowser: false);
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
        if (_exiting || !_settings.CloseToTray || TrayIconHost is null)
        {
            return;
        }

        args.Cancel = true;
        AppWindow.Hide();

        if (!_settings.TrayTipShown)
        {
            AppendLog("已收进托盘：双击图标可唤回控制台，右键菜单可退出。");
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

        TrayIconHost?.Dispose();

        try
        {
            Close();
        }
        catch
        {
        }

        Application.Current.Exit();
    }

    // ---------- 更新检查 ----------

    private const string LatestReleaseApi =
        "https://api.github.com/repos/Panda35064/workbuddy-remote-winui3/releases/latest";

    private const string ReleasesPage =
        "https://github.com/Panda35064/workbuddy-remote-winui3/releases/latest";

    private string _updateUrl = ReleasesPage;

    /// <summary>只做提示，不静默替换：有新版就弹 InfoBar，点“下载”打开 Release 页。</summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WorkBuddyRemote");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            var json = await client.GetStringAsync(LatestReleaseApi).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(tag))
            {
                return;
            }

            if (!TryParseVersion(tag, out var remote))
            {
                return;
            }

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            if (remote <= new Version(current.Major, current.Minor, Math.Max(0, current.Build)))
            {
                return;
            }

            _updateUrl = string.IsNullOrWhiteSpace(htmlUrl) ? ReleasesPage : htmlUrl;
            Dispatch(() =>
            {
                UpdateBar.Title = $"发现新版本 {tag}";
                UpdateBar.Message = $"当前版本 {current.ToString(3)}，可在 Releases 页下载安装包。";
                UpdateBar.IsOpen = true;
            });
        }
        catch
        {
            // 网络不可达 / API 限流都不打扰用户。
        }
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var text = tag.Trim().TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash > 0)
        {
            text = text[..dash];
        }
        return Version.TryParse(text, out version!);
    }

    private void OnDownloadUpdateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_updateUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("打开下载页失败：" + ex.Message);
        }
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
        TrayIconHost.ToolTipText = portOpen ? $"WorkBuddy Remote · 运行中（:{_settings.BridgePort}）" : "WorkBuddy Remote · 已停止";
        if (TrayToggleBridgeItem is not null)
        {
            TrayToggleBridgeItem.Text = _bridge.IsRunning ? "停止桥接" : "启动桥接";
            TrayRestartBridgeItem.IsEnabled = _bridge.IsRunning;
        }
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
        BuildText.Text = DescribeBuild();
        LogPathText.Text = _settings.RuntimeTempDir;
    }

    /// <summary>CI 构建显示 commit 与编译时间，安装版显示发布版本号。</summary>
    private static string DescribeBuild()
    {
        var exePath = Environment.ProcessPath ?? "";
        var version = typeof(MainWindow).Assembly.GetName().Version;
        var versionText = version is null ? "2.0.0" : version.ToString(3);
        var installedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "WorkBuddyRemote");
        if (exePath.StartsWith(installedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return $"Release {versionText}";
        }

        var built = File.Exists(exePath) ? File.GetLastWriteTime(exePath).ToString("MM-dd HH:mm") : "";
        var commit = TryReadGitCommit(exePath);
        if (string.IsNullOrEmpty(commit))
        {
            return string.IsNullOrEmpty(built) ? "CI" : $"CI · {built}";
        }
        return string.IsNullOrEmpty(built) ? $"CI {commit}" : $"CI {commit} · {built}";
    }

    private static string TryReadGitCommit(string startPath)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(startPath) ?? "");
            for (var depth = 0; directory is not null && depth < 8; depth += 1, directory = directory.Parent)
            {
                var headPath = Path.Combine(directory.FullName, ".git", "HEAD");
                if (!File.Exists(headPath))
                {
                    continue;
                }

                var head = File.ReadAllText(headPath).Trim();
                if (!head.StartsWith("ref:", StringComparison.Ordinal))
                {
                    return head.Length >= 7 ? head[..7] : head;
                }

                var target = head[4..].Trim();
                var refPath = Path.Combine(directory.FullName, ".git", target.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(refPath))
                {
                    var sha = File.ReadAllText(refPath).Trim();
                    return sha.Length >= 7 ? sha[..7] : sha;
                }

                var packedPath = Path.Combine(directory.FullName, ".git", "packed-refs");
                if (File.Exists(packedPath))
                {
                    foreach (var line in File.ReadAllLines(packedPath))
                    {
                        var parts = line.Split(' ');
                        if (parts.Length == 2 && parts[1] == target)
                        {
                            return parts[0].Length >= 7 ? parts[0][..7] : parts[0];
                        }
                    }
                }
                return "";
            }
        }
        catch
        {
            // 读不到就当作没有，不影响主流程。
        }
        return "";
    }

    /// <summary>本机版本 + 与适配版本的比对提示，升级 WorkBuddy 后一眼能看出是否需要复验。</summary>
    private static string DescribeInstalledWorkBuddy(string installed)
    {
        if (string.IsNullOrWhiteSpace(installed))
        {
            return "未检测到";
        }

        var parts = installed.Split('.');
        var adapted = AppInfo.AdaptedWorkBuddyVersion.Split('.');
        if (parts.Length >= 2 && adapted.Length >= 2 &&
            (!string.Equals(parts[0], adapted[0], StringComparison.Ordinal) ||
             !string.Equals(parts[1], adapted[1], StringComparison.Ordinal)))
        {
            return $"{installed}（未验证，适配 {AppInfo.AdaptedWorkBuddyVersion}）";
        }

        return installed;
    }

    private void PopulateAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutVersionText.Text = $"版本 {version?.ToString(3) ?? "2.0.0"} · .NET {Environment.Version.Major} · Windows App SDK";
        AboutAdaptedText.Text = AppInfo.AdaptedWorkBuddyLabel;

        var installed = WorkBuddyLocator.DescribeInstalledVersion(_settings.WorkBuddyExePath);
        AboutInstalledText.Text = DescribeInstalledWorkBuddy(installed);

        AboutProjectText.Text = _projectRoot;
        AboutConfigText.Text = _settings.ConfigPath + (_settings.IsPortable ? "（便携版）" : "");
        AboutLogsText.Text = _settings.RuntimeTempDir;
        AboutPortsText.Text = $"桥接 {_settings.BridgePort} · CDP {_settings.CdpPort}";
    }

    // ---------- 命令 ----------

    private async void OnStartClick(object sender, RoutedEventArgs e)
        => await RunExclusiveAsync("启动中", () => StartCoreAsync(openBrowser: false));

    private async void OnRestartBridgeClick(object sender, RoutedEventArgs e)
        => await RunExclusiveAsync("重启中", () => StartCoreAsync(openBrowser: false, restartWorkBuddy: true));

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

    private void OnOpenRepoClick(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/Panda35064/workbuddy-remote-winui3");

    private void OnOpenIssuesClick(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/Panda35064/workbuddy-remote-winui3/issues");

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("打开链接失败：" + ex.Message);
        }
    }

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

    // ---------- 诊断包 ----------

    private async void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
        => await RunExclusiveAsync("导出中", ExportDiagnosticsAsync);

    /// <summary>日志 + 脱敏配置 + 系统信息打成一个 zip，方便发 issue。</summary>
    private async Task ExportDiagnosticsAsync()
    {
        AppendLog("正在导出诊断包…");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = $"WorkBuddyRemote-diagnostics-{stamp}.zip";
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var targetDir = Directory.Exists(desktop)
            ? desktop
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var targetPath = Path.Combine(targetDir, fileName);

        try
        {
            using var zip = System.IO.Compression.ZipFile.Open(
                targetPath, System.IO.Compression.ZipArchiveMode.Create);

            // 日志（桥接正在写这些文件，必须用共享读打开）
            if (Directory.Exists(_settings.RuntimeTempDir))
            {
                foreach (var file in Directory.EnumerateFiles(_settings.RuntimeTempDir, "*", SearchOption.TopDirectoryOnly))
                {
                    var info = new FileInfo(file);
                    if (info.Length > 20 * 1024 * 1024)
                    {
                        continue;
                    }

                    try
                    {
                        using var source = new FileStream(file, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        var entry = zip.CreateEntry("logs/" + info.Name,
                            System.IO.Compression.CompressionLevel.Optimal);
                        using var destination = entry.Open();
                        source.CopyTo(destination);
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"跳过日志 {info.Name}：{ex.Message}");
                    }
                }
            }

            // 脱敏配置
            var configJson = "{}";
            try
            {
                if (File.Exists(_settings.ConfigPath))
                {
                    configJson = RedactConfig(File.ReadAllText(_settings.ConfigPath));
                }
            }
            catch
            {
            }
            WriteEntry(zip, "config.redacted.json", configJson);

            // 系统信息
            var info2 = new StringBuilder();
            info2.AppendLine($"时间: {DateTime.Now:O}");
            info2.AppendLine($"应用版本: {Assembly.GetExecutingAssembly().GetName().Version}");
            info2.AppendLine($"OS: {Environment.OSVersion.VersionString} (build {Environment.OSVersion.Version.Build})");
            info2.AppendLine($"64位进程: {Environment.Is64BitProcess}");
            info2.AppendLine($".NET: {Environment.Version}");
            info2.AppendLine($"程序目录: {AppContext.BaseDirectory}");
            info2.AppendLine($"项目目录: {_projectRoot}");
            info2.AppendLine($"配置: {_settings.ConfigPath}");
            info2.AppendLine($"便携模式: {_settings.IsPortable}");
            info2.AppendLine($"桥接端口: {_settings.BridgePort} / 监听: {_settings.ListenHost}");
            info2.AppendLine($"CDP 端口: {_settings.CdpPort}（监听中: {await NetProbe.PortOpenAsync(_settings.CdpPort)}）");
            info2.AppendLine($"桥接端口监听中: {await NetProbe.PortOpenAsync(_settings.BridgePort)}");
            info2.AppendLine($"桥接进程 PID: {_bridge.ProcessId}");
            info2.AppendLine($"WorkBuddy 主程序: {_settings.WorkBuddyExePath}");
            info2.AppendLine($"WorkBuddy 版本: {WorkBuddyLocator.DescribeInstalledVersion(_settings.WorkBuddyExePath)}");
            info2.AppendLine($"node: {BridgeHost.ResolveNodeExecutable(_projectRoot)}");
            WriteEntry(zip, "system.txt", info2.ToString());

            AppendLog("诊断包已导出：" + targetPath);
            OpenFolder(targetDir);
            SetStatus(InfoBarSeverity.Success, "诊断包已导出", targetPath);
        }
        catch (Exception ex)
        {
            // 别把半成品留在桌面
            try
            {
                if (File.Exists(targetPath)) File.Delete(targetPath);
            }
            catch
            {
            }

            AppendLog("导出诊断包失败：" + ex.Message);
            SetStatus(InfoBarSeverity.Error, "导出失败", ex.Message);
        }
    }

    private static void WriteEntry(System.IO.Compression.ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string RedactConfig(string json)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node is not System.Text.Json.Nodes.JsonObject obj)
            {
                return "{}";
            }

            foreach (var key in obj.Select(pair => pair.Key).ToList())
            {
                // 只打码字符串型敏感字段；布尔开关名里带 "Secrets" 不代表它本身敏感。
                if (obj[key]?.GetValueKind() != System.Text.Json.JsonValueKind.String)
                {
                    continue;
                }

                if (key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("secret", StringComparison.OrdinalIgnoreCase))
                {
                    obj[key] = "***";
                }
            }

            return obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return "{}";
        }
    }

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
        => await RunExclusiveAsync("重启中", () => StartCoreAsync(openBrowser: false, restartWorkBuddy: true));

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
        WorkDaddyPathBox.Text = _settings.WorkDaddyLauncherPath;
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

        var isLan = ScopeBox.SelectedIndex == 1;
        // 仅本机不需要密码，直接把这一整行收起来，避免误以为还要维护密码。
        if (PasswordCard is not null)
        {
            PasswordCard.Visibility = isLan ? Visibility.Visible : Visibility.Collapsed;
        }
        PasswordBox1.PlaceholderText = isLan ? "访问密码（至少 4 位）" : "未设置";
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
        _settings.WorkDaddyLauncherPath = WorkDaddyPathBox.Text.Trim();
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

        if (result.Ok && _settings.StartWorkDaddy)
        {
            StartWorkDaddyIfNeeded();
        }

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

/// <summary>最简单的 ICommand 实现，供托盘双击唤回使用。</summary>
internal sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => action();
}
