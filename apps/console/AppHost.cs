using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WorkBuddyRemote;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0xF4, 0xF6, 0xF9);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(0xE3, 0xE7, 0xED);
    public static readonly Color Text = Color.FromArgb(0x16, 0x1A, 0x1F);
    public static readonly Color Muted = Color.FromArgb(0x6B, 0x74, 0x82);
    public static readonly Color Faint = Color.FromArgb(0x9C, 0xA5, 0xB2);
    public static readonly Color Accent = Color.FromArgb(0x2B, 0x6B, 0xE4);
    public static readonly Color AccentHover = Color.FromArgb(0x1F, 0x59, 0xC8);
    public static readonly Color AccentPress = Color.FromArgb(0x19, 0x4A, 0xAA);
    public static readonly Color Green = Color.FromArgb(0x15, 0x9A, 0x5A);
    public static readonly Color GreenSoft = Color.FromArgb(0xE8, 0xF7, 0xEE);
    public static readonly Color Amber = Color.FromArgb(0xA9, 0x6A, 0x06);
    public static readonly Color AmberSoft = Color.FromArgb(0xFF, 0xF6, 0xE4);
    public static readonly Color Red = Color.FromArgb(0xD1, 0x44, 0x3A);
    public static readonly Color GraySoft = Color.FromArgb(0xEE, 0xF1, 0xF5);

    public const string UiFont = "Microsoft YaHei UI";
}

internal static class Gfx
{
    public static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        if (d > rect.Width) d = rect.Width;
        if (d > rect.Height) d = rect.Height;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class AppSettings
{
    public string ProjectRoot { get; set; } = "";
    public int BridgePort { get; set; } = 8780;
    public int CdpPort { get; set; } = 9333;
    public string ListenHost { get; set; } = "127.0.0.1";
    public string RuntimeTempDir { get; set; } = "";
    public string? LanAddress { get; set; }

    public string WorkBuddyExePath { get; set; } = "";
    public string WorkBuddyUserDataDir { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool HideWorkBuddyWindowAfterStart { get; set; }
    public bool StartWorkDaddy { get; set; }
    public string WorkDaddyLauncherPath { get; set; } = "";
    public bool AutoRestartWorkBuddy { get; set; } = true;
    public bool KillWorkBuddyProcessesBeforeStart { get; set; }
    public bool EnableFileManager { get; set; } = true;
    public bool EnableRestart { get; set; } = true;
    public bool MaskBridgeModelSecrets { get; set; }
    public string RuntimeRootDir { get; set; } = "";

    // v2 新增：进程与托盘行为
    public bool CloseToTray { get; set; } = true;
    public bool KeepBridgeOnExit { get; set; }
    public bool TrayTipShown { get; set; }

    /// <summary>本次加载是否套用了安装器写下的设置（用于启动日志提示）。</summary>
    public bool InstallerOverridesApplied { get; set; }

    public string ConfigPath => ResolveConfigPath();

    /// <summary>
    /// 安装版写 %APPDATA%\WorkBuddyRemote\config.json；便携版（exe 旁有 portable.flag）
    /// 写程序目录，方便整个文件夹拷走。
    /// </summary>
    public static string ResolveConfigPath()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable.flag")))
        {
            return Path.Combine(exeDir, "config.json");
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "WorkBuddyRemote", "config.json");
    }

    public bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));

    public bool IsLan => !IsLoopbackHost(ListenHost);
    public bool HasPassword => !string.IsNullOrWhiteSpace(PasswordHash);
    public string LocalUrl => $"http://127.0.0.1:{BridgePort}/agent-manager/";
    public string? LanUrl => IsLan && !string.IsNullOrWhiteSpace(LanAddress)
        ? $"http://{LanAddress}:{BridgePort}/agent-manager/"
        : null;
    public string HealthUrl => $"http://{(IsLan ? "127.0.0.1" : ListenHost)}:{BridgePort}/healthz";
    public string ReadyUrl => $"http://{(IsLan ? "127.0.0.1" : ListenHost)}:{BridgePort}/readyz";
    public string ListenLabel => IsLan ? "局域网" : "仅本机";

    private static bool IsLoopbackHost(string host)
    {
        var value = (host ?? "").Trim().ToLowerInvariant();
        return value is "127.0.0.1" or "localhost" or "::1";
    }

    public static AppSettings Load(string projectRoot)
    {
        int bridgePort = 8780;
        int cdpPort = 9333;
        string listenHost = "127.0.0.1";
        string runtimeRoot = Path.Combine(projectRoot, "output", "runtime");
        string runtimeRootConfig = "";
        string exePath = "";
        string userDataDir = "";
        string passwordHash = "";
        bool hideWindow = false;
        bool startWorkDaddy = false;
        string workDaddyLauncherPath = "";
        bool autoRestartWorkBuddy = true;
        bool killBeforeStart = false;
        bool enableFileManager = true;
        bool enableRestart = true;
        bool maskSecrets = false;
        bool closeToTray = true;
        bool keepBridgeOnExit = false;
        bool trayTipShown = false;

        var configPath = ResolveConfigPath();
        var imported = false;
        if (!File.Exists(configPath))
        {
            imported = TryImportLegacyConfig(projectRoot, configPath);
        }

        try
        {
            if (File.Exists(configPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("bridgePort", out var bp) && bp.ValueKind == JsonValueKind.Number && bp.GetInt32() > 0)
                {
                    bridgePort = bp.GetInt32();
                }
                if (root.TryGetProperty("cdpPort", out var cp) && cp.ValueKind == JsonValueKind.Number && cp.GetInt32() > 0)
                {
                    cdpPort = cp.GetInt32();
                }
                if (root.TryGetProperty("listenHost", out var lh) && lh.ValueKind == JsonValueKind.String)
                {
                    var value = lh.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) listenHost = value.Trim();
                }
                if (root.TryGetProperty("runtimeRootDir", out var rr) && rr.ValueKind == JsonValueKind.String)
                {
                    var value = rr.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        runtimeRootConfig = value.Trim();
                        runtimeRoot = Path.IsPathRooted(value) ? value : Path.Combine(projectRoot, value);
                    }
                }
                exePath = ReadString(root, "workbuddyExePath");
                userDataDir = ReadString(root, "workbuddyUserDataDir");
                passwordHash = ReadString(root, "passwordHash");
                hideWindow = ReadBool(root, "hideWorkBuddyWindowAfterStart", true);
                startWorkDaddy = ReadBool(root, "startWorkDaddy", false);
                workDaddyLauncherPath = ReadString(root, "workDaddyLauncherPath");
                autoRestartWorkBuddy = ReadBool(root, "autoRestartWorkBuddy", true);
                killBeforeStart = ReadBool(root, "killWorkBuddyProcessesBeforeStart", false);
                enableFileManager = ReadBool(root, "enableFileManager", true);
                enableRestart = ReadBool(root, "enableRestart", true);
                maskSecrets = ReadBool(root, "maskBridgeModelSecrets", false);
                closeToTray = ReadBool(root, "closeToTray", true);
                keepBridgeOnExit = ReadBool(root, "keepBridgeOnExit", false);
                trayTipShown = ReadBool(root, "trayTipShown", false);
            }
        }
        catch
        {
            // 配置损坏时退回默认值，保存时会用当前值重写。
        }

        string tempDir;
        try
        {
            tempDir = Path.Combine(Path.GetFullPath(runtimeRoot), "temp");
        }
        catch
        {
            tempDir = Path.Combine(projectRoot, "output", "runtime", "temp");
        }

        var settings = new AppSettings
        {
            ProjectRoot = projectRoot,
            BridgePort = bridgePort,
            CdpPort = cdpPort,
            ListenHost = listenHost,
            RuntimeTempDir = tempDir,
            LanAddress = FindLanAddress(),
            WorkBuddyExePath = exePath,
            WorkBuddyUserDataDir = userDataDir,
            PasswordHash = passwordHash,
            HideWorkBuddyWindowAfterStart = hideWindow,
            StartWorkDaddy = startWorkDaddy,
            WorkDaddyLauncherPath = workDaddyLauncherPath,
            AutoRestartWorkBuddy = autoRestartWorkBuddy,
            KillWorkBuddyProcessesBeforeStart = killBeforeStart,
            EnableFileManager = enableFileManager,
            EnableRestart = enableRestart,
            MaskBridgeModelSecrets = maskSecrets,
            RuntimeRootDir = runtimeRootConfig,
            CloseToTray = closeToTray,
            KeepBridgeOnExit = keepBridgeOnExit,
            TrayTipShown = trayTipShown,
        };

        // 首次运行：把探测到的 WorkBuddy 写回配置，后续启动不再猜。
        if (string.IsNullOrWhiteSpace(settings.WorkBuddyExePath))
        {
            var detected = WorkBuddyLocator.GuessExecutable();
            if (!string.IsNullOrWhiteSpace(detected))
            {
                settings.WorkBuddyExePath = detected;
            }
        }

        // 安装器选择的端口 / 访问范围 / 密码，在这里落地并删除中间文件。
        var overridesPath = Path.Combine(Path.GetDirectoryName(settings.ConfigPath)!, InstallerOverridesFile);
        var overridesApplied = ApplyInstallerOverrides(settings, overridesPath);
        settings.InstallerOverridesApplied = overridesApplied;

        // 首次运行（或刚从 v1 导入、或刚应用安装器设置）：立刻落盘一份配置，
        // 用户和桥接进程都能直接看到这个文件。
        var saved = false;
        if (imported || overridesApplied || !File.Exists(settings.ConfigPath))
        {
            try
            {
                settings.Save();
                saved = true;
            }
            catch
            {
            }
        }

        // 只有真正写进配置后才删中间文件；写失败就留着下次重试。
        if (overridesApplied && saved)
        {
            try
            {
                File.Delete(overridesPath);
            }
            catch
            {
            }
        }

        return settings;
    }

    private const string InstallerOverridesFile = "install-overrides.json";

    /// <summary>
    /// 应用安装器写下的 install-overrides.json（端口 / 访问范围 / 密码）。
    /// 密码在安装器里以 UTF-16 十六进制传递，这里还原后生成与桥接一致的哈希。
    /// </summary>
    private static bool ApplyInstallerOverrides(AppSettings settings, string overridesPath)
    {
        try
        {
            if (!File.Exists(overridesPath))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(overridesPath));
            var root = doc.RootElement;

            if (root.TryGetProperty("bridgePort", out var bp) && bp.ValueKind == JsonValueKind.Number &&
                bp.GetInt32() is >= 1 and <= 65535)
            {
                settings.BridgePort = bp.GetInt32();
            }

            if (root.TryGetProperty("cdpPort", out var cp) && cp.ValueKind == JsonValueKind.Number &&
                cp.GetInt32() is >= 1 and <= 65535)
            {
                settings.CdpPort = cp.GetInt32();
            }

            var host = ReadString(root, "listenHost");
            if (!string.IsNullOrWhiteSpace(host))
            {
                settings.ListenHost = host;
            }

            var passwordHex = ReadString(root, "passwordUtf16Hex");
            if (!string.IsNullOrWhiteSpace(passwordHex) && DecodeUtf16Hex(passwordHex) is { Length: > 0 } password)
            {
                settings.PasswordHash = Passwords.CreateHash(password);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? DecodeUtf16Hex(string hex)
    {
        if (hex.Length == 0 || hex.Length % 4 != 0)
        {
            return null;
        }

        var bytes = new byte[hex.Length / 2];
        for (int i = 0, b = 0; i < hex.Length; i += 4, b += 2)
        {
            if (!ushort.TryParse(hex.AsSpan(i, 4), System.Globalization.NumberStyles.HexNumber, null, out var unit))
            {
                return null;
            }

            bytes[b] = (byte)(unit & 0xFF);
            bytes[b + 1] = (byte)(unit >> 8);
        }

        return Encoding.Unicode.GetString(bytes);
    }

    /// <summary>首次运行时把 v1 的项目根配置导入到新位置，路径类字段失效则丢弃。</summary>
    private static bool TryImportLegacyConfig(string projectRoot, string targetPath)
    {
        try
        {
            var legacyPath = Path.Combine(projectRoot, "workbuddy-remote.config.json");
            if (!File.Exists(legacyPath) || string.Equals(legacyPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var text = File.ReadAllText(legacyPath);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var obj = new System.Text.Json.Nodes.JsonObject();
            foreach (var name in new[] { "bridgePort", "cdpPort", "listenHost", "passwordHash",
                                         "hideWorkBuddyWindowAfterStart", "enableFileManager",
                                         "enableRestart", "maskBridgeModelSecrets",
                                         "killWorkBuddyProcessesBeforeStart", "autoRestartWorkBuddy" })
            {
                if (root.TryGetProperty(name, out var element))
                {
                    obj[name] = System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText());
                }
            }

            var exe = root.TryGetProperty("workbuddyExePath", out var exeElement) ? (exeElement.GetString() ?? "") : "";
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                obj["workbuddyExePath"] = exe;
            }

            var userData = root.TryGetProperty("workbuddyUserDataDir", out var udElement) ? (udElement.GetString() ?? "") : "";
            if (!string.IsNullOrWhiteSpace(userData) && Directory.Exists(userData))
            {
                obj["workbuddyUserDataDir"] = userData;
            }

            if (root.TryGetProperty("startWorkDaddy", out var wd))
            {
                obj["startWorkDaddy"] = wd.ValueKind == JsonValueKind.True;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllText(targetPath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
                new System.Text.UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return "";
        return (element.GetString() ?? "").Trim();
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var element)) return fallback;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString()?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on",
            _ => fallback,
        };
    }

    /// <summary>把当前设置写回配置文件，保留文件中其它未知字段。写临时文件后原子替换。</summary>
    public void Save()
    {
        var node = new System.Text.Json.Nodes.JsonObject();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var parsed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ConfigPath));
                if (parsed is System.Text.Json.Nodes.JsonObject obj) node = obj;
            }
        }
        catch
        {
        }

        node["workbuddyExePath"] = WorkBuddyExePath;
        node["workbuddyUserDataDir"] = WorkBuddyUserDataDir;
        if (!string.IsNullOrWhiteSpace(RuntimeRootDir)) node["runtimeRootDir"] = RuntimeRootDir;
        node["cdpPort"] = CdpPort;
        node["bridgePort"] = BridgePort;
        node["listenHost"] = ListenHost;
        node["killWorkBuddyProcessesBeforeStart"] = KillWorkBuddyProcessesBeforeStart;
        node["hideWorkBuddyWindowAfterStart"] = HideWorkBuddyWindowAfterStart;
        node["startWorkDaddy"] = StartWorkDaddy;
        node["autoRestartWorkBuddy"] = AutoRestartWorkBuddy;
        node["closeToTray"] = CloseToTray;
        node["keepBridgeOnExit"] = KeepBridgeOnExit;
        node["trayTipShown"] = TrayTipShown;
        if (string.IsNullOrWhiteSpace(WorkDaddyLauncherPath))
        {
            node.Remove("workDaddyLauncherPath");
        }
        else
        {
            node["workDaddyLauncherPath"] = WorkDaddyLauncherPath;
        }
        node["maskBridgeModelSecrets"] = MaskBridgeModelSecrets;
        node["enableFileManager"] = EnableFileManager;
        node["enableRestart"] = EnableRestart;
        if (string.IsNullOrWhiteSpace(PasswordHash))
        {
            node.Remove("passwordHash");
        }
        else
        {
            node["passwordHash"] = PasswordHash;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = node.ToJsonString(options) + Environment.NewLine;
        var targetDir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrWhiteSpace(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        var tempPath = ConfigPath + ".tmp";
        File.WriteAllText(tempPath, json, new System.Text.UTF8Encoding(false));
        File.Move(tempPath, ConfigPath, overwrite: true);

        RuntimeTempDir = Path.Combine(
            string.IsNullOrWhiteSpace(RuntimeRootDir)
                ? Path.Combine(ProjectRoot, "output", "runtime")
                : Path.IsPathRooted(RuntimeRootDir) ? RuntimeRootDir : Path.Combine(ProjectRoot, RuntimeRootDir),
            "temp");
    }

    public static string? FindLanAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = unicast.Address.ToString();
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                    return ip;
                }
            }
        }
        catch
        {
        }
        return null;
    }
}

internal static class ProjectLocator
{
    private static string ExeDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private static string IniPath => Path.Combine(ExeDir, "WorkBuddyRemote.ini");

    public static bool IsProjectRoot(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        return File.Exists(Path.Combine(dir, "bridge", "main.mjs"));
    }

    public static string? Resolve()
    {
        if (IsProjectRoot(ExeDir)) return ExeDir;

        // 开发目录：apps/console/bin/x64/Release/<tfm>/ 向上最多 6 层能找到仓库根。
        var dir = new DirectoryInfo(ExeDir);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            if (IsProjectRoot(dir.FullName)) return Path.GetFullPath(dir.FullName);
            dir = dir.Parent;
        }

        foreach (var line in ReadIniLines())
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#") || trimmed.StartsWith(";")) continue;
            var index = trimmed.IndexOf('=');
            if (index <= 0) continue;
            if (!trimmed[..index].Trim().Equals("ProjectRoot", StringComparison.OrdinalIgnoreCase)) continue;
            var value = trimmed[(index + 1)..].Trim().Trim('"');
            try
            {
                if (IsProjectRoot(value)) return Path.GetFullPath(value);
            }
            catch
            {
            }
        }

        var candidates = new[]
        {
            @"D:\Program Files\Tools\workbuddy-remote-v2",
            @"D:\Program Files\Tools\workbuddy-remote",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "workbuddy-remote"),
        };
        foreach (var candidate in candidates)
        {
            if (IsProjectRoot(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static IEnumerable<string> ReadIniLines()
    {
        try
        {
            return File.Exists(IniPath) ? File.ReadAllLines(IniPath) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static void Remember(string projectRoot)
    {
        try
        {
            File.WriteAllText(IniPath, "# WorkBuddy Remote 控制台的项目路径记录" + Environment.NewLine +
                                      "ProjectRoot=" + projectRoot + Environment.NewLine);
        }
        catch
        {
            // 无写入权限时忽略，下次启动会再次询问。
        }
    }
}

internal static class NetProbe
{
    public static async Task<bool> HttpOkAsync(string url)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(1200) };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> PortOpenAsync(int port, int timeoutMs = 300)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            var completed = await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false);
            return completed == connect && connect.IsCompletedSuccessfully && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static bool ProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}

internal static class Passwords
{
    /// <summary>与 src/server/password-hash.mjs 保持一致：sha256:&lt;salt&gt;:&lt;hex(sha256(password + salt))&gt;。</summary>
    public static string CreateHash(string password)
    {
        var salt = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12))
            .ToLowerInvariant();
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(password + salt));
        return "sha256:" + salt + ":" + Convert.ToHexString(digest).ToLowerInvariant();
    }
}

internal static class WorkBuddyLocator
{
    /// <summary>探测顺序：运行中的进程 → 注册表卸载项 → 常见安装目录。</summary>
    public static string? GuessExecutable()
    {
        var fromProcess = FindFromRunningProcess();
        if (!string.IsNullOrWhiteSpace(fromProcess)) return fromProcess;

        var fromRegistry = FindFromRegistry();
        if (!string.IsNullOrWhiteSpace(fromRegistry)) return fromRegistry;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WorkBuddy", "WorkBuddy.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WorkBuddy", "WorkBuddy.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WorkBuddy", "WorkBuddy.exe"),
            @"D:\Program Files\Dev\WorkBuddy\WorkBuddy.exe",
        };
        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
            }
        }
        return null;
    }

    private static string? FindFromRunningProcess()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("WorkBuddy"))
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                }
                catch
                {
                    // 位数不同或权限不足时读不到 MainModule，继续下一个。
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string? FindFromRegistry()
    {
        var roots = new[]
        {
            (Microsoft.Win32.RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, subPath) in roots)
        {
            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Default);
                using var uninstall = baseKey.OpenSubKey(subPath);
                if (uninstall is null) continue;

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var sub = uninstall.OpenSubKey(name);
                    var display = sub?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(display) ||
                        display.IndexOf("WorkBuddy", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    var icon = sub?.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrWhiteSpace(icon))
                    {
                        var path = icon.Trim().Trim('"');
                        var comma = path.LastIndexOf(',');
                        if (comma > 0 && path.Length - comma < 6)
                        {
                            path = path[..comma];
                        }
                        if (File.Exists(path)) return path;
                    }

                    var location = sub?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(location))
                    {
                        var candidate = Path.Combine(location.Trim().Trim('"'), "WorkBuddy.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch
            {
            }
        }
        return null;
    }

    public static bool IsRunning()
    {
        try
        {
            var list = Process.GetProcessesByName("WorkBuddy");
            foreach (var process in list) process.Dispose();
            return list.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void KillAll()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("WorkBuddy"))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
        }
    }

    public static string DescribeInstalledVersion(string? exePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return "";
            return FileVersionInfo.GetVersionInfo(exePath).ProductVersion ?? "";
        }
        catch
        {
            return "";
        }
    }
}
