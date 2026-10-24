using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace WorkBuddyRemote.WinUI;

public sealed partial class MainWindow : Window
{
    private const int ScriptStillRunning = int.MinValue;

    private readonly string _projectRoot;
    private AppSettings _settings;
    private readonly bool _autostart;
    private readonly DispatcherTimer _timer = new();
    private bool _busy;
    private bool _initialized;
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

        _timer.Interval = TimeSpan.FromSeconds(2.5);
        _timer.Tick += async (_, _) => await RefreshStatusAsync();

        RootGrid.Loaded += OnRootLoaded;
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

        AppendLog($"控制台已就绪，项目目录：{_projectRoot}");
        AppendLog($"桥接端口 {_settings.BridgePort} · CDP 端口 {_settings.CdpPort} · 监听 {_settings.ListenHost}");

        await RefreshStatusAsync();
        _timer.Start();

        if (_autostart)
        {
            if (!await NetProbe.HttpOkAsync(_settings.HealthUrl))
            {
                await RunExclusiveAsync("启动中", () => StartCoreAsync(openBrowser: false));
            }

            // 开机静默启动：不弹到前台，最小化到任务栏。
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
            SetStatus(InfoBarSeverity.Success, "运行中", $"网页控制台：{_settings.LocalUrl}");
        }
        else
        {
            SetStatus(InfoBarSeverity.Warning, "启动中", "端口已监听，正在等待桥接就绪…");
        }

        WorkDaddyText.Text = await DetectWorkDaddyAsync();
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
        AboutVersionText.Text = $"版本 {version?.ToString(3) ?? "1.0.0"} · .NET 8 · Windows App SDK";
        AboutProjectText.Text = _projectRoot;
        AboutConfigText.Text = _settings.ConfigPath;
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

        _settings.Save();
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
        if (!File.Exists(_settings.StartScript))
        {
            AppendLog("找不到启动脚本：" + _settings.StartScript);
            return;
        }

        AppendLog(restartWorkBuddy ? "正在重启 WorkBuddy 并启动桥接…" : "正在启动桥接服务…");
        var psi = CreatePowerShellInfo(_settings.StartScript);
        if (openBrowser)
        {
            psi.ArgumentList.Add("-OpenBrowser");
        }
        if (restartWorkBuddy)
        {
            psi.ArgumentList.Add("-RestartWorkBuddy");
        }

        using var process = new Process { StartInfo = psi };
        WireOutput(process);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var exitCode = await WaitForScriptExitAsync(process, TimeSpan.FromMinutes(3));
        if (exitCode == ScriptStillRunning)
        {
            AppendLog("启动脚本仍在后台运行，桥接就绪后状态会自动更新。");
            return;
        }

        switch (exitCode)
        {
            case 0:
                AppendLog("桥接服务已就绪 → " + _settings.LocalUrl);
                return;
            case 42:
                AppendLog("WorkBuddy 正在运行，但没有开启调试端口（需要重启一次）。");
                if (restartWorkBuddy)
                {
                    AppendLog("重启后仍未开启调试端口，请检查是否有其他 WorkBuddy 实例占用单实例锁。");
                    return;
                }
                if (_autostart && _settings.AutoRestartWorkBuddy)
                {
                    AppendLog("开机静默启动：正在自动重启 WorkBuddy 以开启调试端口…");
                    await StartCoreAsync(openBrowser, restartWorkBuddy: true);
                    return;
                }
                if (_autostart)
                {
                    AppendLog("已跳过自动重启（设置里已关闭）。可在控制台点“重启 WorkBuddy”。");
                    return;
                }
                await StartCoreAsync(openBrowser, restartWorkBuddy: true);
                return;
            case 43:
                AppendLog("WorkBuddy 未能在限定时间内开启 CDP 端口，请检查是否有旧实例占用单实例锁。");
                return;
            default:
                AppendLog($"启动失败（退出码 {exitCode}），详情见日志目录。");
                return;
        }
    }

    private async Task StopCoreAsync()
    {
        if (!File.Exists(_settings.StopScript))
        {
            AppendLog("找不到停止脚本：" + _settings.StopScript);
            return;
        }

        AppendLog("正在停止桥接服务…");
        var psi = CreatePowerShellInfo(_settings.StopScript);
        psi.ArgumentList.Add("-BridgePort");
        psi.ArgumentList.Add(_settings.BridgePort.ToString());
        psi.ArgumentList.Add("-ProjectRoot");
        psi.ArgumentList.Add(_settings.ProjectRoot);

        using var process = new Process { StartInfo = psi };
        WireOutput(process);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await WaitForScriptExitAsync(process, TimeSpan.FromSeconds(90));

        for (var i = 0; i < 25; i++)
        {
            if (!await NetProbe.PortOpenAsync(_settings.BridgePort))
            {
                break;
            }
            await Task.Delay(200);
        }

        AppendLog(await NetProbe.HttpOkAsync(_settings.HealthUrl) ? "桥接服务仍在响应，请查看日志。" : "桥接服务已停止。");
    }

    private void WireOutput(Process process)
    {
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                AppendLog("  " + e.Data.Trim());
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                AppendLog("  " + e.Data.Trim());
            }
        };
    }

    private static ProcessStartInfo CreatePowerShellInfo(string scriptPath)
    {
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(shell))
        {
            shell = "powershell.exe";
        }

        var psi = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        return psi;
    }

    private async Task<int> WaitForScriptExitAsync(Process process, TimeSpan timeout)
    {
        process.EnableRaisingEvents = true;
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExited(object? sender, EventArgs args)
        {
            try
            {
                completion.TrySetResult(process.ExitCode);
            }
            catch
            {
                completion.TrySetResult(ScriptStillRunning);
            }
        }

        process.Exited += OnExited;
        try
        {
            if (process.HasExited)
            {
                completion.TrySetResult(process.ExitCode);
            }

            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout));
            if (finished != completion.Task)
            {
                AppendLog($"等待脚本结束超时（{timeout.TotalSeconds:0} 秒），已不再阻塞界面。");
                return ScriptStillRunning;
            }

            return await completion.Task;
        }
        finally
        {
            process.Exited -= OnExited;
        }
    }

    private static string PromptForProjectRoot()
    {
        var guess = @"D:\Program Files\Tools\workbuddy-remote";
        if (Directory.Exists(guess))
        {
            return guess;
        }

        return AppContext.BaseDirectory;
    }
}
