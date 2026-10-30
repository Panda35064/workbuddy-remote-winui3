using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace WorkBuddyRemote.WinUI;

/// <summary>
/// 定位本机已安装的 WorkDaddy。Windows 上"安装位置"没有统一出口，
/// 按可靠性从高到低依次尝试：运行中的进程 → App Paths → 卸载注册表 → 开始菜单快捷方式 → PATH → 常见目录。
/// </summary>
internal static class WorkDaddyLocator
{
    private const string ExeName = "WorkDaddy.exe";

    private static readonly string[] UninstallKeys =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    /// <summary>返回 exe 路径与命中来源；找不到时返回 (null, 说明)。</summary>
    public static (string? Path, string Source) Find(string? preferred = null)
    {
        var manual = (preferred ?? "").Trim().Trim('"');
        if (manual.Length > 0)
        {
            if (File.Exists(manual))
            {
                return (manual, "手动指定");
            }
            // 用户填了目录而不是 exe 时，补一层文件名再试。
            if (Directory.Exists(manual))
            {
                var fromDir = Path.Combine(manual, ExeName);
                if (File.Exists(fromDir))
                {
                    return (fromDir, "手动指定目录");
                }
            }
        }

        if (FromRunningProcess() is { } running)
        {
            return (running, "运行中的进程");
        }

        if (FromAppPaths() is { } appPath)
        {
            return (appPath, "注册表 App Paths");
        }

        if (FromUninstallEntries() is { } uninstall)
        {
            return (uninstall, "注册表卸载项");
        }

        if (FromStartMenuShortcuts() is { } shortcut)
        {
            return (shortcut, "开始菜单快捷方式");
        }

        if (FromPathEnvironment() is { } fromPath)
        {
            return (fromPath, "PATH 环境变量");
        }

        if (FromCommonPaths() is { } common)
        {
            return (common, "常见安装目录");
        }

        return (null, "未找到");
    }

    private static string? FromRunningProcess()
    {
        foreach (var processName in new[] { "WorkDaddy", "workdaddy", "WorkDaddyLauncher", "WorkDaddy AI" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    var file = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
                    {
                        return file;
                    }
                }
                catch
                {
                    // 32/64 位不一致或权限不足时读不到 MainModule，跳过。
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        return null;
    }

    /// <summary>App Paths 是安装器注册可执行文件的规范位置，一个键就能拿到完整路径。</summary>
    private static string? FromAppPaths()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + ExeName
                    );
                    var value = (key?.GetValue(null) as string ?? "").Trim().Trim('"');
                    if (value.Length > 0 && File.Exists(value))
                    {
                        return value;
                    }
                }
                catch
                {
                }
            }
        }
        return null;
    }

    private static string? FromUninstallEntries()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var subKeyPath in UninstallKeys)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    using var uninstall = baseKey.OpenSubKey(subKeyPath);
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var entry = uninstall.OpenSubKey(name);
                        var displayName = entry?.GetValue("DisplayName") as string ?? "";
                        if (!displayName.Contains("WorkDaddy", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // DisplayIcon 常见形态："C:\Path\App.exe",0
                        var icon = entry?.GetValue("DisplayIcon") as string;
                        var fromIcon = NormalizeExePath(icon);
                        if (fromIcon is not null && File.Exists(fromIcon))
                        {
                            return fromIcon;
                        }

                        var location = (entry?.GetValue("InstallLocation") as string ?? "").Trim().Trim('"');
                        if (location.Length > 0 && Directory.Exists(location))
                        {
                            var direct = Path.Combine(location, ExeName);
                            if (File.Exists(direct))
                            {
                                return direct;
                            }

                            // 部分安装包只放一个 Launcher（例如 WorkDaddyLauncher.exe），
                            // 或者子目录里才是主程序，这里扫一层兜底，Launcher 优先。
                            var fromDirectory = ScanInstallDirectory(location);
                            if (fromDirectory is not null)
                            {
                                return fromDirectory;
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }
        return null;
    }

    /// <summary>在安装目录里找可执行文件：优先 Launcher，其次其它 WorkDaddy*.exe，排除卸载程序。</summary>
    private static string? ScanInstallDirectory(string directory)
    {
        try
        {
            var candidates = Directory
                .EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.Contains("WorkDaddy", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("卸载", StringComparison.OrdinalIgnoreCase)
                        && !name.StartsWith("unins", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(path =>
                    Path.GetFileName(path).Contains("Launcher", StringComparison.OrdinalIgnoreCase)
                )
                .ToList();

            return candidates.Count > 0 ? candidates[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FromStartMenuShortcuts()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var link in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    if (!Path.GetFileNameWithoutExtension(link).Contains("WorkDaddy", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var target = ResolveShortcutTarget(link);
                    if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
                    {
                        return target;
                    }
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private static string? FromPathEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), ExeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private static string? FromCommonPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "WorkDaddy", ExeName),
            Path.Combine(localAppData, "WorkDaddy", ExeName),
            Path.Combine(localAppData, "Programs", "WorkDaddy AI", ExeName),
            Path.Combine(roamingAppData, "WorkDaddy", ExeName),
            Path.Combine(programFiles, "WorkDaddy", ExeName),
            Path.Combine(programFilesX86, "WorkDaddy", ExeName),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>解析 .lnk 的目标路径（WScript.Shell 的 CreateShortcut）。</summary>
    private static string? ResolveShortcutTarget(string linkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            var shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            var shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { linkPath }
            );
            if (shortcut is null)
            {
                return null;
            }

            var target = shortcut.GetType()
                .InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeExePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        // 去掉 ",0" 这类图标索引后缀
        var comma = value.LastIndexOf(',');
        if (comma > 0 && value.EndsWith("0", StringComparison.Ordinal))
        {
            var withoutIndex = value[..comma].Trim().Trim('"');
            if (File.Exists(withoutIndex))
            {
                return withoutIndex;
            }
        }

        var cleaned = value.Trim('"');
        return cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? cleaned : null;
    }
}
