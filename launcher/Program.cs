namespace WorkBuddyRemote;

internal static class Program
{
    private const string MutexName = @"Local\WorkBuddyRemoteLauncher";
    private const string ShowEventName = @"Local\WorkBuddyRemoteLauncher.Show";

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var mutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch
            {
            }
            return;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        var projectRoot = ProjectLocator.Resolve();
        if (projectRoot is null)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "请选择 WorkBuddy Remote 项目目录（内含 tools\\start-workbuddy-remote.ps1）",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };
            const string guess = @"D:\Program Files\Tools\workbuddy-remote";
            if (Directory.Exists(guess)) dialog.SelectedPath = guess;

            if (dialog.ShowDialog() != DialogResult.OK || !ProjectLocator.IsProjectRoot(dialog.SelectedPath))
            {
                MessageBox.Show("未找到可用的 WorkBuddy Remote 项目目录，程序退出。", "WorkBuddy Remote",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            projectRoot = dialog.SelectedPath;
            ProjectLocator.Remember(projectRoot);
        }

        var autostart = args.Any(arg => arg.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        var settings = AppSettings.Load(projectRoot);

        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowFatal(e.ExceptionObject as Exception);

        var renderPath = ReadArgumentValue(args, "--render");
        if (renderPath is not null)
        {
            RenderToFile(settings, renderPath);
            return;
        }

        var renderSettingsPath = ReadArgumentValue(args, "--render-settings");
        if (renderSettingsPath is not null)
        {
            RenderFormToFile(new SettingsForm(settings), renderSettingsPath);
            return;
        }

        using var form = new MainForm(settings, autostart, showEvent);
        Application.Run(form);
    }

    /// <summary>开发辅助：把界面离屏渲染成 PNG，便于检查排版（不进入消息循环）。</summary>
    private static void RenderToFile(AppSettings settings, string path)
    {
        try
        {
            using var form = new MainForm(settings, autostart: false, new EventWaitHandle(false, EventResetMode.AutoReset));
            RenderFormToFile(form, path);
        }
        catch (Exception error)
        {
            File.WriteAllText(path + ".txt", error.ToString());
        }
    }

    private static void RenderFormToFile(Form form, string path)
    {
        try
        {
            form.Show();
            for (var i = 0; i < 40; i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);

            var info = new System.Text.StringBuilder();
            info.AppendLine($"deviceDpi={form.DeviceDpi} client={form.ClientSize} size={form.Size}");
            foreach (Control control in form.Controls)
            {
                info.AppendLine($"{control.GetType().Name,-16} {control.Bounds} dpi={control.DeviceDpi}");
            }
            File.WriteAllText(path + ".txt", info.ToString());

            form.Close();
        }
        catch (Exception error)
        {
            File.WriteAllText(path + ".txt", error.ToString());
        }
    }

    private static string? ReadArgumentValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(name.Length + 1)..];
            }
            if (arg.Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static void ShowFatal(Exception? error)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "WorkBuddyRemote-error.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {error}{Environment.NewLine}");
        }
        catch
        {
        }

        try
        {
            MessageBox.Show("控制台遇到未处理的错误：" + Environment.NewLine + Environment.NewLine +
                            (error?.Message ?? "未知错误"),
                "WorkBuddy Remote", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
        }
    }
}
