using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace WorkBuddyRemote;

internal sealed class MainForm : Form
{
    private readonly EventWaitHandle _showEvent;
    private readonly bool _autostart;
    private readonly string _logFilePath;
    private AppSettings _settings;

    private readonly StatusPill _pill = new();
    private readonly InfoCard _card = new();
    private readonly TextBox _logBox = new();
    private readonly CheckBox _autoRun = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    private readonly RoundedButton _startButton = new();
    private readonly RoundedButton _restartButton = new();
    private readonly RoundedButton _stopButton = new();
    private readonly RoundedButton _openButton = new();
    private readonly RoundedButton _configButton = new();
    private readonly RoundedButton _restartAppButton = new();
    private readonly RoundedButton _logsButton = new();

    private NotifyIcon? _tray;
    private Icon? _appIcon;
    private bool _busy;
    private bool _refreshing;
    private bool _trayHintShown;
    private bool _exiting;

    /// <summary>布局以 96 DPI 为基准，按当前 DPI 统一放大，避免字体与控件尺寸不同步。</summary>
    private readonly float _scale;

    private int S(int value) => (int)Math.Round(value * _scale);
    private Point P(int x, int y) => new(S(x), S(y));
    private Rectangle R(int x, int y, int width, int height) => new(S(x), S(y), S(width), S(height));

    public MainForm(AppSettings settings, bool autostart, EventWaitHandle showEvent)
    {
        _settings = settings;
        _autostart = autostart;
        _showEvent = showEvent;
        _scale = DeviceDpi / 96f;
        _logFilePath = Path.Combine(settings.RuntimeTempDir, "launcher.log");

        AutoScaleMode = AutoScaleMode.None;

        Text = "WorkBuddy Remote 控制台";
        ClientSize = new Size(S(660), S(536));
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Theme.Bg;
        Font = new Font(Theme.UiFont, 9f);
        DoubleBuffered = true;

        try
        {
            _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (_appIcon is not null) Icon = _appIcon;
        }
        catch
        {
        }

        BuildUi();
        BuildTray();

        _timer.Interval = 2500;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        AppendLog("控制台已就绪，项目目录：" + _settings.ProjectRoot);
        AppendLog($"桥接端口 {_settings.BridgePort} · CDP 端口 {_settings.CdpPort} · 监听 {_settings.ListenHost}");
        AppendVersionLine();

        var waiter = new Thread(() =>
        {
            while (true)
            {
                _showEvent.WaitOne();
                try
                {
                    BeginInvoke(new Action(ShowFromTray));
                }
                catch
                {
                    return;
                }
            }
        })
        { IsBackground = true, Name = "show-signal" };
        waiter.Start();

        Shown += async (_, _) => await RefreshAsync();
    }

    private void BuildUi()
    {
        var title = new Label
        {
            Text = "WorkBuddy Remote",
            Font = new Font(Theme.UiFont, 15f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = P(24, 13),
            BackColor = Color.Transparent,
        };
        var subtitle = new Label
        {
            Text = "本地桥接控制台 · 关闭窗口会收进托盘，桥接服务继续运行",
            Font = new Font(Theme.UiFont, 8.75f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Location = P(26, 45),
            BackColor = Color.Transparent,
        };

        _pill.Location = P(516, 20);
        _pill.Size = new Size(S(120), S(30));
        _pill.SetState("检测中…", Theme.GraySoft, Theme.Muted);

        _card.Location = P(24, 78);
        _card.Size = new Size(S(612), S(108));
        _card.UrlClicked += (_, _) => CopyUrl();

        _startButton.Text = "启动";
        _startButton.Variant = ButtonVariant.Primary;
        _startButton.Bounds = R(24, 202, 112, 40);
        _startButton.Click += async (_, _) => await StartCommandAsync();

        _restartButton.Text = "重启桥接";
        _restartButton.Bounds = R(146, 202, 100, 40);
        _restartButton.Click += async (_, _) => await RestartCommandAsync();

        _stopButton.Text = "停止";
        _stopButton.Variant = ButtonVariant.DangerOutline;
        _stopButton.Bounds = R(256, 202, 88, 40);
        _stopButton.Click += async (_, _) => await StopCommandAsync();

        _openButton.Text = "打开页面";
        _openButton.Variant = ButtonVariant.AccentOutline;
        _openButton.Bounds = R(354, 202, 112, 40);
        _openButton.Click += (_, _) => OpenWeb();

        _logsButton.Text = "日志";
        _logsButton.Bounds = R(476, 202, 78, 40);
        _logsButton.Click += (_, _) => OpenLogFolder();

        _configButton.Text = "设置";
        _configButton.Bounds = R(24, 252, 96, 40);
        _configButton.Click += (_, _) => OpenSettings();

        _restartAppButton.Text = "重启 WorkBuddy";
        _restartAppButton.Bounds = R(130, 252, 140, 40);
        _restartAppButton.Click += async (_, _) => await RestartWorkBuddyCommandAsync();

        var logTitle = new Label
        {
            Text = "运行日志",
            Font = new Font(Theme.UiFont, 8.75f, FontStyle.Bold),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Location = P(26, 308),
            BackColor = Color.Transparent,
        };
        var clearLink = new LinkLabel
        {
            Text = "清空",
            Font = new Font(Theme.UiFont, 8.5f),
            AutoSize = true,
            LinkBehavior = LinkBehavior.HoverUnderline,
            LinkColor = Theme.Faint,
            ActiveLinkColor = Theme.Accent,
            VisitedLinkColor = Theme.Faint,
            BackColor = Color.Transparent,
        };
        clearLink.Location = new Point(Width - S(24) - clearLink.PreferredWidth - S(2), S(308));
        clearLink.LinkClicked += (_, _) => _logBox.Clear();

        var logCard = new CardBox
        {
            Location = P(24, 328),
            Size = new Size(S(612), S(150)),
            Padding = new Padding(S(10), S(9), S(9), S(9)),
        };

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.BorderStyle = BorderStyle.None;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.WordWrap = true;
        _logBox.BackColor = Theme.Card;
        _logBox.ForeColor = Color.FromArgb(0x33, 0x3B, 0x46);
        _logBox.Font = new Font(Theme.UiFont, 8.5f);
        _logBox.Dock = DockStyle.Fill;
        logCard.Controls.Add(_logBox);

        _autoRun.Text = "开机自动启动（静默启动桥接）";
        _autoRun.Font = new Font(Theme.UiFont, 8.75f);
        _autoRun.ForeColor = Theme.Muted;
        _autoRun.BackColor = Theme.Bg;
        _autoRun.FlatStyle = FlatStyle.Flat;
        _autoRun.AutoSize = true;
        _autoRun.Location = P(24, 494);
        _autoRun.Checked = File.Exists(StartupShortcutPath);
        _autoRun.CheckedChanged += (_, _) => ApplyAutoRun(_autoRun.Checked);

        var folderLink = new LinkLabel
        {
            Text = "打开项目目录",
            Font = new Font(Theme.UiFont, 8.5f),
            AutoSize = true,
            LinkBehavior = LinkBehavior.HoverUnderline,
            LinkColor = Theme.Faint,
            ActiveLinkColor = Theme.Accent,
            VisitedLinkColor = Theme.Faint,
            BackColor = Theme.Bg,
        };
        folderLink.Location = new Point(Width - S(24) - folderLink.PreferredWidth, S(495));
        folderLink.LinkClicked += (_, _) => OpenFolder(_settings.ProjectRoot);

        Controls.AddRange(new Control[]
        {
            title, subtitle, _pill, _card,
            _startButton, _restartButton, _stopButton, _openButton, _configButton, _restartAppButton, _logsButton,
            logTitle, clearLink, logCard,
            _autoRun, folderLink,
        });

        ApplySettingsToUi();
    }

    private void ApplySettingsToUi()
    {
        _card.LocalUrl = _settings.LocalUrl;
        _card.LanUrl = _settings.LanUrl;
        _card.BridgePort = _settings.BridgePort;
        _card.CdpPort = _settings.CdpPort;
        _card.IsLan = _settings.IsLan;
        _card.Invalidate();
    }

    private void BuildTray()
    {
        if (_appIcon is null) return;

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开控制台", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("启动桥接服务", null, async (_, _) => await StartCommandAsync());
        menu.Items.Add("重启桥接服务", null, async (_, _) => await RestartCommandAsync());
        menu.Items.Add("停止桥接服务", null, async (_, _) => await StopCommandAsync());
        menu.Items.Add("打开页面", null, (_, _) => OpenWeb());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置…", null, (_, _) =>
        {
            ShowFromTray();
            OpenSettings();
        });
        menu.Items.Add("编辑配置文件", null, (_, _) => OpenConfig());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出控制台", null, (_, _) => ExitApp());

        _tray = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "WorkBuddy Remote",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private static string StartupShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "WorkBuddy Remote.lnk");

    // ---------- 状态 ----------

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var bridgeTask = NetProbe.HttpOkAsync(_settings.HealthUrl);
            var cdpTask = NetProbe.PortOpenAsync(_settings.CdpPort);
            await Task.WhenAll(bridgeTask, cdpTask);

            var bridgeReady = bridgeTask.Result;
            var cdpReady = cdpTask.Result;

            _card.BridgeReady = bridgeReady;
            _card.CdpReady = cdpReady;
            _card.WorkBuddyRunning = cdpReady || NetProbe.ProcessRunning("WorkBuddy");
            _card.Invalidate();

            if (!_busy)
            {
                ApplyIdleState(bridgeReady, cdpReady);
            }
            if (_tray is not null)
            {
                _tray.Text = bridgeReady
                    ? $"WorkBuddy Remote · 运行中（{_settings.BridgePort}）"
                    : "WorkBuddy Remote · 已停止";
            }
        }
        catch
        {
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyIdleState(bool bridgeReady, bool cdpReady)
    {
        if (bridgeReady)
        {
            _pill.SetState("运行中", Theme.GreenSoft, Theme.Green);
        }
        else if (NetProbe.ProcessRunning("WorkBuddy") && !cdpReady)
        {
            _pill.SetState("待重启", Theme.AmberSoft, Theme.Amber);
        }
        else
        {
            _pill.SetState("已停止", Theme.GraySoft, Theme.Muted);
        }
    }

    private void SetBusy(string? busyText)
    {
        _busy = busyText is not null;
        if (_busy)
        {
            _pill.SetState(busyText!, Theme.AmberSoft, Theme.Amber);
        }
        foreach (var button in new[] { _startButton, _restartButton, _stopButton, _openButton, _configButton, _restartAppButton, _logsButton })
        {
            button.Enabled = !_busy;
        }
        Cursor = _busy ? Cursors.AppStarting : Cursors.Default;
    }

    private async Task RunExclusiveAsync(string busyText, Func<Task> action)
    {
        if (_busy) return;
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
            await RefreshAsync();
        }
    }

    // ---------- 命令 ----------

    private async Task StartCommandAsync()
    {
        if (_busy) return;
        if (await NetProbe.HttpOkAsync(_settings.HealthUrl))
        {
            AppendLog("桥接服务已在运行，直接打开页面。");
            OpenWeb();
            return;
        }
        await RunExclusiveAsync("启动中", () => StartCoreAsync(true));
    }

    private async Task RestartWorkBuddyCommandAsync()
    {
        if (_busy) return;
        if (!ConfirmRestartWorkBuddy()) return;
        await RunExclusiveAsync("重启中", () => StartCoreAsync(true, restartWorkBuddy: true));
    }

    private async Task RestartCommandAsync()
    {
        if (_busy) return;
        await RunExclusiveAsync("重启中", async () =>
        {
            await StopCoreAsync();
            await Task.Delay(400);
            await StartCoreAsync(true);
        });
    }

    private async Task StopCommandAsync()
    {
        if (_busy) return;
        await RunExclusiveAsync("停止中", StopCoreAsync);
    }

    private async Task StartCoreAsync(bool openBrowser, bool restartWorkBuddy = false)
    {
        if (!File.Exists(_settings.StartScript))
        {
            AppendLog("找不到启动脚本：" + _settings.StartScript);
            return;
        }

        AppendLog(restartWorkBuddy ? "正在重启 WorkBuddy 并启动桥接…" : "正在启动桥接服务…");
        var psi = CreatePowerShellInfo(_settings.StartScript);
        if (openBrowser) psi.ArgumentList.Add("-OpenBrowser");
        if (restartWorkBuddy) psi.ArgumentList.Add("-RestartWorkBuddy");

        using var process = new Process { StartInfo = psi };
        WireOutput(process);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var exitCode = await WaitForScriptExitAsync(process, TimeSpan.FromMinutes(3));
        if (exitCode == ScriptStillRunning)
        {
            AppendLog("启动脚本仍在后台运行，桥接服务就绪后状态会自动更新。");
            return;
        }

        switch (exitCode)
        {
            case 0:
                AppendLog("桥接服务已就绪 → " + _settings.LocalUrl);
                Notify("桥接服务已启动", _settings.LocalUrl);
                return;
            case 42:
                AppendLog("WorkBuddy 正在运行，但没有开启调试端口（新版启动方式已变更，需要重启一次）。");
                if (restartWorkBuddy)
                {
                    AppendLog("重启后仍未开启调试端口，请检查是否有其他 WorkBuddy 实例占用了单实例锁。");
                    return;
                }

                // 开机静默启动时 WorkBuddy 往往已经被它自己的自启动拉起（没有调试端口），
                // 这时候不能等用户来点，直接自动重启一次。
                if (_autostart && _settings.AutoRestartWorkBuddy)
                {
                    AppendLog("开机静默启动：正在自动重启 WorkBuddy 以开启调试端口…");
                    await StartCoreAsync(openBrowser, restartWorkBuddy: true);
                    return;
                }

                if (_autostart)
                {
                    Notify("需要重启 WorkBuddy", "打开控制台并点击“重启 WorkBuddy”，桥接才能连上。");
                    return;
                }

                if (ConfirmRestartWorkBuddy())
                {
                    await StartCoreAsync(openBrowser, restartWorkBuddy: true);
                }
                return;
            case 43:
                AppendLog("WorkBuddy 未能在限定时间内开启 CDP 端口，请检查是否有旧实例占用了单实例锁。");
                Notify("启动失败", "WorkBuddy 没有开启调试端口。");
                return;
            default:
                AppendLog($"启动失败（退出码 {exitCode}），详情见日志目录。");
                Notify("启动失败", "请查看运行日志与日志目录。");
                return;
        }
    }

    private bool ConfirmRestartWorkBuddy()
    {
        var version = WorkBuddyLocator.DescribeInstalledVersion(_settings.WorkBuddyExePath);
        var text =
            "重启 WorkBuddy 会关闭当前的 WorkBuddy 窗口，正在进行的对话会中断（会话记录会保留）。" +
            Environment.NewLine + Environment.NewLine +
            "先重启一次后，桥接就能拿到调试端口；之后正常打开 WorkBuddy 也会自动带上调试端口。" +
            (string.IsNullOrWhiteSpace(version) ? "" : Environment.NewLine + Environment.NewLine + "当前版本：" + version) +
            Environment.NewLine + Environment.NewLine + "现在重启吗？";

        return MessageBox.Show(this, text, "重启 WorkBuddy", MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
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
            if (!await NetProbe.PortOpenAsync(_settings.BridgePort)) break;
            await Task.Delay(200);
        }

        if (await NetProbe.HttpOkAsync(_settings.HealthUrl))
        {
            AppendLog("桥接服务仍在响应，请查看日志确认是否已停止。");
        }
        else
        {
            AppendLog("桥接服务已停止。");
        }
    }

    /// <summary>脚本自身还在跑时的返回值（不是真实的进程退出码）。</summary>
    private const int ScriptStillRunning = int.MinValue;

    /// <summary>
    /// 等待脚本进程退出。
    /// 不能用 WaitForExit()：它要等重定向管道读到 EOF，而脚本拉起的 WorkBuddy 会继承
    /// 这个管道句柄，脚本退出后管道仍然开着，界面就会永远停在“启动中”。
    /// </summary>
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

            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout)).ConfigureAwait(true);
            if (finished != completion.Task)
            {
                AppendLog($"等待脚本结束超时（{timeout.TotalSeconds:0} 秒），已不再阻塞界面。");
                return ScriptStillRunning;
            }

            return await completion.Task.ConfigureAwait(true);
        }
        finally
        {
            process.Exited -= OnExited;
        }
    }

    private void WireOutput(Process process)
    {
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) AppendLog("  " + e.Data.Trim());
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) AppendLog("  " + e.Data.Trim());
        };
    }

    private static ProcessStartInfo CreatePowerShellInfo(string scriptPath)
    {
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(shell)) shell = "powershell.exe";

        var psi = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
        };
        ApplyCleanEnvironment(psi);
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-STA");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        return psi;
    }

    /// <summary>
    /// Windows PowerShell 5.1 的 Start-Process 在环境块里存在大小写重复的变量
    /// （比如同时有 HTTP_PROXY 和 http_proxy）时会抛「已添加项」异常，
    /// 而启动脚本内部正是用 Start-Process 拉起 WorkBuddy 和 bridge，所以这里先去重。
    /// </summary>
    private static void ApplyCleanEnvironment(ProcessStartInfo psi)
    {
        try
        {
            var resolved = new Dictionary<string, (string Key, string Value)>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is not string key) continue;
                var value = entry.Value as string ?? "";
                if (resolved.TryGetValue(key, out var existing))
                {
                    // 同一个变量名出现多种大小写时，优先保留全大写写法。
                    if (key == key.ToUpperInvariant()) resolved[key] = (key, value);
                    continue;
                }
                resolved[key] = (key, value);
            }

            psi.Environment.Clear();
            foreach (var item in resolved.Values)
            {
                psi.Environment[item.Key] = item.Value;
            }
        }
        catch
        {
            // 去重失败时保留原环境，让脚本自己报错。
        }
    }

    private void CopyUrl()
    {
        try
        {
            Clipboard.SetText(_settings.LocalUrl);
            AppendLog("已复制访问地址：" + _settings.LocalUrl);
        }
        catch (Exception ex)
        {
            AppendLog("复制失败：" + ex.Message);
        }
    }

    private void OpenWeb()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_settings.LocalUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("打开浏览器失败：" + ex.Message);
        }
    }

    private void OpenSettings()
    {
        var previousPort = _settings.BridgePort;
        var previousHost = _settings.ListenHost;
        var previousCdp = _settings.CdpPort;

        using (var dialog = new SettingsForm(_settings))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
        }

        ReloadSettings();
        var needsRestart = previousPort != _settings.BridgePort ||
                           previousCdp != _settings.CdpPort ||
                           !string.Equals(previousHost, _settings.ListenHost, StringComparison.OrdinalIgnoreCase);
        if (needsRestart)
        {
            AppendLog("端口或访问方式已变更，请点击“重启桥接”后生效。");
        }
    }

    private void OpenConfig()
    {
        try
        {
            if (!File.Exists(_settings.ConfigPath))
            {
                AppendLog("找不到配置文件：" + _settings.ConfigPath);
                return;
            }
            var process = Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_settings.ConfigPath}\"")
            {
                UseShellExecute = true,
            });
            if (process is null) return;
            AppendLog("已打开配置文件，保存并关闭记事本后自动重新加载。");
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                try
                {
                    BeginInvoke(new Action(ReloadSettings));
                }
                catch
                {
                }
            };
        }
        catch (Exception ex)
        {
            AppendLog("打开配置失败：" + ex.Message);
        }
    }

    private void ReloadSettings()
    {
        _settings = AppSettings.Load(_settings.ProjectRoot);
        ApplySettingsToUi();
        AppendLog($"配置已重新加载：端口 {_settings.BridgePort}，监听 {_settings.ListenHost}。若服务已在运行，请点击重启生效。");
        _ = RefreshAsync();
    }

    private void AppendVersionLine()
    {
        var exe = string.IsNullOrWhiteSpace(_settings.WorkBuddyExePath)
            ? WorkBuddyLocator.GuessExecutable()
            : _settings.WorkBuddyExePath;
        var version = WorkBuddyLocator.DescribeInstalledVersion(exe);
        if (!string.IsNullOrWhiteSpace(version))
        {
            AppendLog("WorkBuddy " + version + " · 通过 WORKBUDDY_REMOTE_DEBUGGING_PORT 开启调试端口");
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.RuntimeTempDir);
            OpenFolder(_settings.RuntimeTempDir);
        }
        catch (Exception ex)
        {
            AppendLog("打开日志目录失败：" + ex.Message);
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    // ---------- 开机自启 ----------

    private bool _applyingAutoRun;

    private void ApplyAutoRun(bool enabled)
    {
        if (_applyingAutoRun) return;
        _applyingAutoRun = true;
        try
        {
            var shortcut = StartupShortcutPath;
            if (enabled)
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null)
                {
                    AppendLog("当前系统不支持创建快捷方式。");
                    _autoRun.Checked = false;
                    return;
                }

                var shell = Activator.CreateInstance(shellType);
                var link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { shortcut });
                if (link is null) throw new InvalidOperationException("创建快捷方式失败");

                var linkType = link.GetType();
                var exePath = Application.ExecutablePath;
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { exePath });
                linkType.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { "--autostart" });
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link,
                    new object[] { Path.GetDirectoryName(exePath) ?? "" });
                linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { exePath });
                linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link,
                    new object[] { "WorkBuddy Remote 控制台" });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);

                AppendLog("已设置开机自动启动（静默启动桥接，不弹浏览器）。");
            }
            else
            {
                if (File.Exists(shortcut)) File.Delete(shortcut);
                AppendLog("已取消开机自动启动。");
            }
        }
        catch (Exception ex)
        {
            AppendLog("设置开机自启失败：" + ex.Message);
            _autoRun.Checked = !enabled;
        }
        finally
        {
            _applyingAutoRun = false;
        }
    }

    // ---------- 日志与托盘 ----------

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action<string>(AppendLog), message);
            }
            catch
            {
            }
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        WriteLogFile(line);
        _logBox.AppendText(line);
        if (_logBox.Lines.Length > 900)
        {
            var keep = _logBox.Lines.Skip(_logBox.Lines.Length - 400).ToArray();
            _logBox.Text = string.Join(Environment.NewLine, keep) + Environment.NewLine;
        }
        if (_logBox.IsHandleCreated)
        {
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }
    }

    private void WriteLogFile(string line)
    {
        try
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var info = new FileInfo(_logFilePath);
            if (info.Exists && info.Length > 512 * 1024)
            {
                var keep = File.ReadAllLines(_logFilePath).TakeLast(800).ToArray();
                File.WriteAllLines(_logFilePath, keep);
            }
            File.AppendAllText(_logFilePath, line);
        }
        catch
        {
        }
    }

    private void Notify(string title, string text)
    {
        if (_tray is null || Visible) return;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.BalloonTipIcon = ToolTipIcon.Info;
        _tray.ShowBalloonTip(4000);
    }

    private void ShowFromTray()
    {
        if (_exiting) return;
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        _ = RefreshAsync();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_autostart) return;

        Hide();
        ShowInTaskbar = false;
        BeginInvoke(new Action(async () =>
        {
            if (await NetProbe.HttpOkAsync(_settings.HealthUrl)) return;
            await RunExclusiveAsync("启动中", () => StartCoreAsync(false));
        }));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && _tray is not null && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.BalloonTipTitle = "控制台已收进托盘";
                _tray.BalloonTipText = "桥接服务继续运行；需要退出请右键托盘图标。";
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(4000);
            }
            return;
        }

        _timer.Stop();
        _timer.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        base.OnFormClosing(e);
    }
}
