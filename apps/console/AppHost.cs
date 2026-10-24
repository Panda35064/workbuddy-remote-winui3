using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    public bool StartWorkDaddy { get; set; } = true;
    public string WorkDaddyLauncherPath { get; set; } = "";
    public bool AutoRestartWorkBuddy { get; set; } = true;
    public bool KillWorkBuddyProcessesBeforeStart { get; set; }
    public bool EnableFileManager { get; set; } = true;
    public bool EnableRestart { get; set; } = true;
    public bool MaskBridgeModelSecrets { get; set; }
    public string RuntimeRootDir { get; set; } = "";

    public string ConfigPath => Path.Combine(ProjectRoot, "workbuddy-remote.config.json");
    public string StartScript => Path.Combine(ProjectRoot, "tools", "start-workbuddy-remote.ps1");
    public string StopScript => Path.Combine(ProjectRoot, "tools", "stop-workbuddy-remote.ps1");

    public bool IsLan => !IsLoopbackHost(ListenHost);
    public bool HasPassword => !string.IsNullOrWhiteSpace(PasswordHash);
    public string LocalUrl => $"http://127.0.0.1:{BridgePort}/agent-manager/";
    public string? LanUrl => IsLan && !string.IsNullOrWhiteSpace(LanAddress)
        ? $"http://{LanAddress}:{BridgePort}/agent-manager/"
        : null;
    public string HealthUrl => $"http://{(IsLan ? "127.0.0.1" : ListenHost)}:{BridgePort}/readyz";
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
        bool startWorkDaddy = true;
        string workDaddyLauncherPath = "";
        bool autoRestartWorkBuddy = true;
        bool killBeforeStart = false;
        bool enableFileManager = true;
        bool enableRestart = true;
        bool maskSecrets = false;

        var configPath = Path.Combine(projectRoot, "workbuddy-remote.config.json");
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
                startWorkDaddy = ReadBool(root, "startWorkDaddy", true);
                workDaddyLauncherPath = ReadString(root, "workDaddyLauncherPath");
                autoRestartWorkBuddy = ReadBool(root, "autoRestartWorkBuddy", true);
                killBeforeStart = ReadBool(root, "killWorkBuddyProcessesBeforeStart", false);
                enableFileManager = ReadBool(root, "enableFileManager", true);
                enableRestart = ReadBool(root, "enableRestart", true);
                maskSecrets = ReadBool(root, "maskBridgeModelSecrets", false);
            }
        }
        catch
        {
            // 配置损坏时退回默认值，启动脚本会给出更具体的错误。
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

        return new AppSettings
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
        };
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

    /// <summary>把当前设置写回配置文件，保留文件中其它未知字段。</summary>
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
        File.WriteAllText(ConfigPath, node.ToJsonString(options) + Environment.NewLine, new System.Text.UTF8Encoding(false));

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
        return File.Exists(Path.Combine(dir, "tools", "start-workbuddy-remote.ps1"));
    }

    public static string? Resolve()
    {
        if (IsProjectRoot(ExeDir)) return ExeDir;

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
            @"D:\Program Files\Tools\workbuddy-remote",
            @"C:\Users\Public\workbuddy-remote-new",
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
    public static string? GuessExecutable()
    {
        var candidates = new[]
        {
            @"D:\Program Files\Dev\WorkBuddy\WorkBuddy.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WorkBuddy", "WorkBuddy.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WorkBuddy", "WorkBuddy.exe"),
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
