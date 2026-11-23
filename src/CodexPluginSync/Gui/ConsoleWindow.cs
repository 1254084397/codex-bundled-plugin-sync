using System;
using System.Runtime.InteropServices;

namespace CodexPluginSync.Gui
{
    /// <summary>
    /// 控制台窗口开关。本程序是 Console 子系统（保证 `--check` 等命令能正常阻塞、
    /// 退出码与管道输出准确），但无参数启动时要进 GUI——那时把控制台窗口藏起来。
    /// </summary>
    public static class ConsoleWindow
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SwHide = 0;

        public static void Hide()
        {
            try
            {
                IntPtr handle = GetConsoleWindow();
                if (handle != IntPtr.Zero)
                {
                    ShowWindow(handle, SwHide);
                }
            }
            catch (Exception)
            {
                // 没有控制台或调用失败都不影响 GUI 启动
            }
        }
    }
}
