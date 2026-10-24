using System.Reflection;

namespace WorkBuddyRemote.WinUI;

internal static class StartupShortcut
{
    private const string ShortcutName = "WorkBuddy Remote 控制台.lnk";
    private const string LegacyShortcutName = "WorkBuddy Remote.lnk";
    // 安装包用纯 ASCII 名字创建启动项，避免安装器脚本里的中文编码问题。
    private const string InstallerShortcutName = "WorkBuddyRemote.lnk";

    private static string StartupFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    private static string ShortcutPath => Path.Combine(StartupFolder, ShortcutName);

    private static string LegacyShortcutPath => Path.Combine(StartupFolder, LegacyShortcutName);

    private static string InstallerShortcutPath => Path.Combine(StartupFolder, InstallerShortcutName);

    public static bool IsEnabled() =>
        File.Exists(ShortcutPath) || File.Exists(LegacyShortcutPath) || File.Exists(InstallerShortcutPath);

    public static void Apply(bool enabled)
    {
        if (!enabled)
        {
            Delete(ShortcutPath);
            Delete(LegacyShortcutPath);
            Delete(InstallerShortcutPath);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            throw new InvalidOperationException("无法确定当前程序路径");
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("系统不支持创建快捷方式");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法创建 WScript.Shell");
        try
        {
            var shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { ShortcutPath }) ?? throw new InvalidOperationException("创建快捷方式失败");

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
            shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { "--autostart" });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                new object[] { Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory });
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                new object[] { "WorkBuddy Remote 控制台（静默启动桥接）" });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, Array.Empty<object>());
        }
        finally
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(shell))
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }

        // 迁移：删掉旧 WinForms 控制台和安装器写的启动项，避免开机拉起多个控制台。
        Delete(LegacyShortcutPath);
        Delete(InstallerShortcutPath);
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删除失败不影响主流程（例如被占用），下次保存会再尝试。
        }
    }
}
