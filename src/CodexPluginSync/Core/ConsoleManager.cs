using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// §M1 WinExe/CLI 控制台挂载管理：
    /// 本程序编译目标为 WinExe（普通用户双击时完全不创建黑框控制台）；
    /// 当由命令行、CI 或自动化脚本带参数调用时，挂载父级调用者的控制台并重置 stdout/stderr。
    /// </summary>
    public static class ConsoleManager
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        private const int ATTACH_PARENT_PROCESS = -1;
        private const int STD_OUTPUT_HANDLE = -11;
        private const int STD_ERROR_HANDLE = -12;

        public static void EnsureConsoleForCli()
        {
            // 尝试挂载父进程控制台
            AttachConsole(ATTACH_PARENT_PROCESS);

            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (Exception) { }

            // 重新绑定 Console.Out 和 Console.Error，确保 Console.WriteLine 正常输出到控制台或管道
            try
            {
                IntPtr stdOut = GetStdHandle(STD_OUTPUT_HANDLE);
                if (stdOut != IntPtr.Zero && stdOut != new IntPtr(-1))
                {
                    SafeFileHandle safeOut = new SafeFileHandle(stdOut, false);
                    FileStream outStream = new FileStream(safeOut, FileAccess.Write);
                    StreamWriter writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };
                    Console.SetOut(writer);
                }
            }
            catch (Exception) { }

            try
            {
                IntPtr stdErr = GetStdHandle(STD_ERROR_HANDLE);
                if (stdErr != IntPtr.Zero && stdErr != new IntPtr(-1))
                {
                    SafeFileHandle safeErr = new SafeFileHandle(stdErr, false);
                    FileStream errStream = new FileStream(safeErr, FileAccess.Write);
                    StreamWriter writer = new StreamWriter(errStream, Encoding.UTF8) { AutoFlush = true };
                    Console.SetError(writer);
                }
            }
            catch (Exception) { }
        }
    }
}
