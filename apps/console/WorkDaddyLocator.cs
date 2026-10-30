using System.Diagnostics;
using Microsoft.Win32;

namespace WorkBuddyRemote.WinUI;

/// <summary>
/// 定位本机已安装的 WorkDaddy：手动指定 → 运行中的进程 → 卸载注册表 → 常见目录。
/// </summary>
internal static class WorkDaddyLocator
{
    private const string ExeName = "WorkDaddy.exe";

    /// <summary>返回 exe 路径与命中来源；找不到时返回 (null, 说明)。</summary>
    public static (string? Path, string Source) Find(string? preferred = null)
    {
        if (FromManual(preferred) is { } manual)
        {
            return (manual, "手动指定");
        }

        if (FromRunningProcess() is { } running)
        {
            return (running, "运行中的进程");
        }

        if (FromUninstallRegistry() is { } installed)
        {
            return (installed, "注册表卸载项");
        }

        if (FromCommonPaths() is { } common)
        {
            return (common, "常见安装目录");
        }

        return (null, "未找到");
    }

    private static string? FromManual(string? preferred)
    {
        var value = (preferred ?? "").Trim().Trim('"');
        if (value.Length == 0)
        {
            return null;
        }

        // 既可以填 exe，也可以只填目录。
        return Directory.Exists(value) ? FindExeIn(value) : (File.Exists(value) ? value : null);
    }

    private static string? FromRunningProcess()
    {
        foreach (var name in new[] { "WorkDaddy", "WorkDaddyLauncher" })
        {
            try
            {
                var process = Process.GetProcessesByName(name).FirstOrDefault();
                if (process is null)
                {
                    continue;
                }

                using (process)
                {
                    var file = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
                    {
                        return file;
                    }
                }
            }
            catch
            {
                // 权限不足或位数不一致时读不到 MainModule，跳过。
            }
        }
        return null;
    }

    /// <summary>读卸载项的 DisplayIcon / InstallLocation，安装器都会写这两个。</summary>
    private static string? FromUninstallRegistry()
    {
        var roots = new[] { Registry.CurrentUser, Registry.LocalMachine };
        var paths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var root in roots)
        {
            foreach (var path in paths)
            {
                try
                {
                    using var uninstall = root.OpenSubKey(path);
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

                        // DisplayIcon 常是 "C:\...\App.exe,0"
                        var icon = (entry?.GetValue("DisplayIcon") as string ?? "").Split(',')[0].Trim().Trim('"');
                        if (File.Exists(icon))
                        {
                            return icon;
                        }

                        var location = (entry?.GetValue("InstallLocation") as string ?? "").Trim().Trim('"');
                        if (Directory.Exists(location) && FindExeIn(location) is { } fromDir)
                        {
                            return fromDir;
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

    /// <summary>目录里找 WorkDaddy*.exe，优先 Launcher，排除卸载程序。</summary>
    private static string? FindExeIn(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(file =>
                {
                    var name = Path.GetFileName(file);
                    return name.Contains("WorkDaddy", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("卸载", StringComparison.OrdinalIgnoreCase)
                        && !name.StartsWith("unins", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(file =>
                    Path.GetFileName(file).Contains("Launcher", StringComparison.OrdinalIgnoreCase)
                )
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FromCommonPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "WorkDaddy"),
            Path.Combine(localAppData, "WorkDaddy"),
            Path.Combine(programFiles, "WorkDaddy"),
        };

        foreach (var directory in candidates.Where(Directory.Exists))
        {
            if (FindExeIn(directory) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}
