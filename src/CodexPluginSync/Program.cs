using System;
using System.Windows.Forms;
using CodexPluginSync.Core;

namespace CodexPluginSync
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            bool isCliAssembly = IsCliBinary();

            // 如果是 CLI 专属程序，且未显式指定 --gui：
            if (isCliAssembly && !IsGuiRequested(args))
            {
                ConsoleManager.EnsureConsoleForCli();
                if (args == null || args.Length == 0)
                {
                    // 无参数调用 CLI 程序：打印使用说明并退出，不打开 GUI
                    Console.Out.Write(Cli.CliRunner.UsageText());
                    return Cli.ExitCodes.Ok;
                }
                return Cli.CliRunner.Run(args);
            }

            // GUI 程序（或显式 --gui）：无参数启动进入 WinForms 主界面（WinExe 目标下绝不产生控制台黑框）
            if (args == null || args.Length == 0 || IsGuiRequested(args))
            {
                try
                {
                    bool isNewInstance;
                    using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, "Local\\CodexPluginSync.Gui.SingleInstance", out isNewInstance))
                    {
                        if (!isNewInstance)
                        {
                            MessageBox.Show("Codex 插件同步已在运行中（可查看屏幕右下角系统托盘图标）。",
                                "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return Cli.ExitCodes.Ok;
                        }

                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        Application.Run(new Gui.MainForm());
                        return Cli.ExitCodes.Ok;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.GetType().Name + ": " + ex.Message,
                        "Codex 插件同步启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return Cli.ExitCodes.Error;
                }
            }

            // GUI 程序在终端中带参数调用：挂载调用方的父级终端控制台
            try
            {
                ConsoleManager.EnsureConsoleForCli();
                return Cli.CliRunner.Run(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Fatal: " + ex.GetType().Name + ": " + ex.Message);
                return Cli.ExitCodes.Error;
            }
        }

        private static bool IsCliBinary()
        {
            try
            {
                System.Reflection.Assembly entry = System.Reflection.Assembly.GetEntryAssembly();
                if (entry != null)
                {
                    string asmName = entry.GetName().Name;
                    if (!string.IsNullOrEmpty(asmName) && asmName.IndexOf("cli", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        private static bool IsGuiRequested(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--gui", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
