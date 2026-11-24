using System;
using System.Windows.Forms;
using CodexPluginSync.Core;

namespace CodexPluginSync
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // 双击（无参数或显式 --gui）→ 直接运行 GUI（WinExe 目标下绝不产生控制台黑框）
            if (args == null || args.Length == 0 || IsGuiRequested(args))
            {
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new Gui.MainForm());
                    return Cli.ExitCodes.Ok;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.GetType().Name + ": " + ex.Message,
                        "Codex 插件同步启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return Cli.ExitCodes.Error;
                }
            }

            // 命令行模式：挂载调用方的父级终端控制台
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
