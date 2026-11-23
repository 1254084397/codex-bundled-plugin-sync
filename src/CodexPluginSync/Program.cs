using System;
using System.Windows.Forms;

namespace CodexPluginSync
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // 双击（无参数）→ GUI；带参数 → 命令行，行为与 M1 完全一致。
            if (args == null || args.Length == 0 || IsGuiRequested(args))
            {
                try
                {
                    Gui.ConsoleWindow.Hide();
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

            try
            {
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
