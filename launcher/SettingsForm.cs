namespace WorkBuddyRemote;

/// <summary>
/// 图形化配置窗口：端口、监听方式、局域网密码、WorkBuddy 路径与功能开关。
/// 保存后写回 workbuddy-remote.config.json（保留未知字段），端口/监听变更需重启桥接生效。
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly float _scale;

    private readonly NumericUpDown _bridgePort = new();
    private readonly NumericUpDown _cdpPort = new();
    private readonly RadioButton _localOnly = new();
    private readonly RadioButton _lanMode = new();
    private readonly TextBox _password = new();
    private readonly TextBox _passwordConfirm = new();
    private readonly TextBox _exePath = new();
    private readonly TextBox _userDataDir = new();
    private readonly CheckBox _hideWindow = new();
    private readonly CheckBox _killBeforeStart = new();
    private readonly CheckBox _enableFileManager = new();
    private readonly CheckBox _enableRestart = new();
    private readonly CheckBox _maskSecrets = new();
    private readonly CheckBox _startWorkDaddy = new();
    private readonly CheckBox _autoRestartWorkBuddy = new();
    private readonly CheckBox _clearPassword = new();
    private readonly Label _lanHint = new();
    private readonly Label _error = new();

    private int S(int value) => (int)Math.Round(value * _scale);
    private Point P(int x, int y) => new(S(x), S(y));
    private Size Sz(int w, int h) => new(S(w), S(h));
    private Rectangle R(int x, int y, int w, int h) => new(S(x), S(y), S(w), S(h));

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        _scale = DeviceDpi / 96f;

        AutoScaleMode = AutoScaleMode.None;
        Text = "设置 · WorkBuddy Remote";
        ClientSize = Sz(580, 648);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Bg;
        Font = new Font(Theme.UiFont, 9f);
        DoubleBuffered = true;

        try
        {
            var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is not null) Icon = icon;
        }
        catch
        {
        }

        BuildUi();
        LoadValues();
    }

    private void BuildUi()
    {
        AddHeader("连接", 14);
        AddLabel("桥接端口", 24, 48);
        AddNumber(_bridgePort, 96, 44, 88);
        AddLabel("CDP 端口", 212, 48);
        AddNumber(_cdpPort, 280, 44, 88);
        AddLabel("桥接端口供浏览器访问；CDP 端口是 WorkBuddy 的调试端口。", 24, 70, Theme.Faint, 532);

        AddDivider(84);

        AddHeader("访问方式", 96);
        _localOnly.Text = "仅本机（127.0.0.1）";
        _localOnly.AutoSize = true;
        _localOnly.Location = P(24, 122);
        _localOnly.BackColor = Color.Transparent;
        _localOnly.CheckedChanged += (_, _) => OnAccessModeChanged();

        _lanMode.Text = "局域网 / 端口映射（允许外部访问）";
        _lanMode.AutoSize = true;
        _lanMode.Location = P(212, 122);
        _lanMode.BackColor = Color.Transparent;
        _lanMode.CheckedChanged += (_, _) => OnAccessModeChanged();

        AddLabel("访问密码", 24, 158);
        _password.UseSystemPasswordChar = true;
        _password.BorderStyle = BorderStyle.FixedSingle;
        _password.Font = new Font(Theme.UiFont, 9f);
        _password.Bounds = R(96, 154, 176, 26);

        AddLabel("确认密码", 292, 158);
        _passwordConfirm.UseSystemPasswordChar = true;
        _passwordConfirm.BorderStyle = BorderStyle.FixedSingle;
        _passwordConfirm.Font = new Font(Theme.UiFont, 9f);
        _passwordConfirm.Bounds = R(364, 154, 192, 26);

        _lanHint.AutoSize = false;
        _lanHint.Bounds = R(24, 188, 532, 64);
        _lanHint.ForeColor = Theme.Faint;
        _lanHint.BackColor = Color.Transparent;
        _lanHint.Font = new Font(Theme.UiFont, 8.5f);

        AddDivider(262);

        AddHeader("WorkBuddy", 274);
        AddLabel("程序路径", 24, 314);
        _exePath.BorderStyle = BorderStyle.FixedSingle;
        _exePath.Font = new Font(Theme.UiFont, 9f);
        _exePath.Bounds = R(96, 310, 380, 26);
        AddSmallButton("浏览…", 482, 309, 74, (_, _) => BrowseExecutable());

        AddLabel("数据目录", 24, 348);
        _userDataDir.BorderStyle = BorderStyle.FixedSingle;
        _userDataDir.Font = new Font(Theme.UiFont, 9f);
        _userDataDir.Bounds = R(96, 344, 380, 26);
        AddSmallButton("浏览…", 482, 343, 74, (_, _) => BrowseUserData());

        AddDivider(384);

        AddHeader("功能", 396);
        var y1 = 424;
        var y2 = 452;
        var y3 = 480;
        var y4 = 508;
        AddCheck(_hideWindow, "启动后隐藏 WorkBuddy 窗口", 24, y1);
        AddCheck(_killBeforeStart, "启动前结束后台 WorkBuddy 进程", 24, y2);
        AddCheck(_enableFileManager, "允许文件管理", 300, y1);
        AddCheck(_enableRestart, "允许页面内重启", 300, y2);
        AddCheck(_maskSecrets, "隐藏模型 API Key", 24, y3);
        AddCheck(_startWorkDaddy, "启动 WorkBuddy 时同时启动 WorkDaddy", 24, y4);
        AddCheck(_autoRestartWorkBuddy, "静默启动时自动重启 WorkBuddy", 300, y4);
        AddCheck(_clearPassword, "清除已保存的访问密码", 300, y3);
        _clearPassword.CheckedChanged += (_, _) =>
        {
            if (_clearPassword.Checked)
            {
                _password.Text = "";
                _passwordConfirm.Text = "";
            }
            UpdateEnabledState();
        };

        _error.AutoSize = false;
        _error.Bounds = R(24, 540, 532, 34);
        _error.ForeColor = Theme.Red;
        _error.BackColor = Color.Transparent;
        _error.Font = new Font(Theme.UiFont, 8.75f);

        var configHint = new Label
        {
            Text = "配置文件：" + _settings.ConfigPath,
            ForeColor = Theme.Faint,
            BackColor = Color.Transparent,
            Font = new Font(Theme.UiFont, 8.25f),
            AutoSize = false,
            Bounds = R(24, 564, 532, 18),
        };

        var cancel = new RoundedButton
        {
            Text = "取消",
            Bounds = R(360, 590, 92, 38),
            DialogResult = DialogResult.Cancel,
        };
        var save = new RoundedButton
        {
            Text = "保存",
            Variant = ButtonVariant.Primary,
            Bounds = R(462, 590, 92, 38),
        };
        save.Click += (_, _) => SaveAndClose();

        Controls.AddRange(new Control[]
        {
            _localOnly, _lanMode, _password, _passwordConfirm, _lanHint,
            _exePath, _userDataDir,
            _hideWindow, _killBeforeStart, _enableFileManager, _enableRestart, _maskSecrets, _clearPassword, _startWorkDaddy, _autoRestartWorkBuddy,
            _error, configHint, cancel, save,
        });

        AcceptButton = save;
        CancelButton = cancel;
    }

    private void AddHeader(string text, int y)
    {
        Controls.Add(new Label
        {
            Text = text,
            Font = new Font(Theme.UiFont, 9.5f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = P(24, y),
            BackColor = Color.Transparent,
        });
    }

    private void AddLabel(string text, int x, int y) => AddLabel(text, x, y, Theme.Muted, 0);

    private void AddLabel(string text, int x, int y, Color color, int width)
    {
        var label = new Label
        {
            Text = text,
            ForeColor = color,
            BackColor = Color.Transparent,
            Font = new Font(Theme.UiFont, 8.75f),
            AutoSize = width <= 0,
        };
        if (width <= 0)
        {
            label.Location = P(x, y);
        }
        else
        {
            label.Bounds = R(x, y, width, 18);
        }
        Controls.Add(label);
    }

    private void AddNumber(NumericUpDown box, int x, int y, int width)
    {
        box.Minimum = 1;
        box.Maximum = 65535;
        box.Font = new Font(Theme.UiFont, 9f);
        box.Bounds = R(x, y, width, 26);
        box.TextAlign = HorizontalAlignment.Left;
        Controls.Add(box);
    }

    private void AddCheck(CheckBox box, string text, int x, int y)
    {
        box.Text = text;
        box.AutoSize = true;
        box.Location = P(x, y);
        box.BackColor = Color.Transparent;
        box.ForeColor = Theme.Muted;
        box.Font = new Font(Theme.UiFont, 8.75f);
        Controls.Add(box);
    }

    private void AddDivider(int y)
    {
        var line = new Panel
        {
            BackColor = Theme.Border,
            Bounds = R(24, y, 532, 1),
        };
        Controls.Add(line);
    }

    private void AddSmallButton(string text, int x, int y, int width, EventHandler onClick)
    {
        var button = new RoundedButton
        {
            Text = text,
            Bounds = R(x, y, width, 28),
            Font = new Font(Theme.UiFont, 8.75f),
        };
        button.Click += onClick;
        Controls.Add(button);
    }

    private void LoadValues()
    {
        _bridgePort.Value = Math.Clamp(_settings.BridgePort, 1, 65535);
        _cdpPort.Value = Math.Clamp(_settings.CdpPort, 1, 65535);
        _lanMode.Checked = _settings.IsLan;
        _localOnly.Checked = !_settings.IsLan;
        _exePath.Text = string.IsNullOrWhiteSpace(_settings.WorkBuddyExePath)
            ? WorkBuddyLocator.GuessExecutable() ?? ""
            : _settings.WorkBuddyExePath;
        _userDataDir.Text = _settings.WorkBuddyUserDataDir;
        _hideWindow.Checked = _settings.HideWorkBuddyWindowAfterStart;
        _startWorkDaddy.Checked = _settings.StartWorkDaddy;
        _autoRestartWorkBuddy.Checked = _settings.AutoRestartWorkBuddy;
        _killBeforeStart.Checked = _settings.KillWorkBuddyProcessesBeforeStart;
        _enableFileManager.Checked = _settings.EnableFileManager;
        _enableRestart.Checked = _settings.EnableRestart;
        _maskSecrets.Checked = _settings.MaskBridgeModelSecrets;
        OnAccessModeChanged();
    }

    private void OnAccessModeChanged()
    {
        UpdateEnabledState();
        UpdateLanHint();
    }

    private void UpdateEnabledState()
    {
        var lan = _lanMode.Checked;
        var canEditPassword = lan && !_clearPassword.Checked;
        _password.Enabled = canEditPassword;
        _passwordConfirm.Enabled = canEditPassword;
        _lanMode.ForeColor = Theme.Text;
        _localOnly.ForeColor = Theme.Text;
    }

    private void UpdateLanHint()
    {
        if (!_lanMode.Checked)
        {
            _lanHint.Text = "仅本机访问时不需要密码。开启局域网后，其它设备（含手机、UU 远程等端口映射）访问都必须先登录。";
            return;
        }

        var address = _settings.LanAddress;
        var port = (int)_bridgePort.Value;
        var lines = new List<string>
        {
            _settings.HasPassword
                ? "当前已设置访问密码；留空表示不修改。"
                : "开启局域网必须设置访问密码。",
        };
        if (!string.IsNullOrWhiteSpace(address))
        {
            lines.Add($"本机地址：http://{address}:{port}/agent-manager/");
        }
        lines.Add("远程访问请把该端口映射出去（如 UU 远程端口映射），再打开映射后的地址。");
        _lanHint.Text = string.Join(Environment.NewLine, lines);
    }

    private void BrowseExecutable()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 WorkBuddy.exe",
            Filter = "WorkBuddy (WorkBuddy.exe)|WorkBuddy.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true,
        };
        var current = _exePath.Text.Trim();
        if (current.Length > 0)
        {
            try
            {
                dialog.InitialDirectory = Path.GetDirectoryName(current);
            }
            catch
            {
            }
        }
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _exePath.Text = dialog.FileName;
        }
    }

    private void BrowseUserData()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 WorkBuddy 用户数据目录（默认 %USERPROFILE%\\.workbuddy\\app）",
            ShowNewFolderButton = false,
        };
        if (_userDataDir.Text.Trim().Length > 0)
        {
            try
            {
                dialog.SelectedPath = _userDataDir.Text.Trim();
            }
            catch
            {
            }
        }
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _userDataDir.Text = dialog.SelectedPath;
        }
    }

    private void SaveAndClose()
    {
        _error.Text = "";
        var bridgePort = (int)_bridgePort.Value;
        var cdpPort = (int)_cdpPort.Value;
        if (bridgePort == cdpPort)
        {
            _error.Text = "桥接端口与 CDP 端口不能相同。";
            return;
        }

        var lan = _lanMode.Checked;
        var newPassword = _password.Text;
        var confirm = _passwordConfirm.Text;
        var hasStoredPassword = _settings.HasPassword;

        if (lan)
        {
            if (_clearPassword.Checked)
            {
                _error.Text = "局域网访问必须保留访问密码，请取消“清除已保存的访问密码”。";
                return;
            }
            if (newPassword.Length > 0 && newPassword != confirm)
            {
                _error.Text = "两次输入的密码不一致。";
                return;
            }
            if (newPassword.Length == 0 && !hasStoredPassword)
            {
                _error.Text = "开启局域网访问前，请设置访问密码。";
                return;
            }
            if (newPassword.Length > 0 && newPassword.Length < 4)
            {
                _error.Text = "密码至少 4 位。";
                return;
            }
        }

        _settings.BridgePort = bridgePort;
        _settings.CdpPort = cdpPort;
        _settings.ListenHost = lan ? "0.0.0.0" : "127.0.0.1";
        _settings.WorkBuddyExePath = _exePath.Text.Trim();
        _settings.WorkBuddyUserDataDir = _userDataDir.Text.Trim();
        _settings.HideWorkBuddyWindowAfterStart = _hideWindow.Checked;
        _settings.StartWorkDaddy = _startWorkDaddy.Checked;
        _settings.AutoRestartWorkBuddy = _autoRestartWorkBuddy.Checked;
        _settings.KillWorkBuddyProcessesBeforeStart = _killBeforeStart.Checked;
        _settings.EnableFileManager = _enableFileManager.Checked;
        _settings.EnableRestart = _enableRestart.Checked;
        _settings.MaskBridgeModelSecrets = _maskSecrets.Checked;
        _settings.LanAddress = AppSettings.FindLanAddress();

        if (_clearPassword.Checked)
        {
            _settings.PasswordHash = "";
        }
        else if (newPassword.Length > 0)
        {
            _settings.PasswordHash = Passwords.CreateHash(newPassword);
        }

        try
        {
            _settings.Save();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _error.Text = "保存失败：" + ex.Message;
        }
    }
}
