using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// §M2 统一子进程执行层：
    /// 1. 严格使用 UseShellExecute=false, CreateNoWindow=true, WindowStyle=Hidden, 重定向标准输入输出；
    /// 2. 避免在 Windows GUI 下弹出任何黑色命令行窗口（PowerShell、robocopy、cipher、cmd 等）；
    /// 3. 支持超时安全终止、robocopy 退出码 0-7 成功与 32/33 占用识别、以及保留 stderr 详细错误。
    /// </summary>
    public static class ProcessRunner
    {
        public sealed class RunResult
        {
            public int ExitCode { get; set; }
            public string StandardOutput { get; set; }
            public string StandardError { get; set; }
            public bool TimedOut { get; set; }
            public bool Success { get; set; }
            public string CommandType { get; set; }
            public string ErrorMessage { get; set; }
        }

        public static RunResult Run(
            string fileName,
            string arguments,
            int timeoutMilliseconds = 30000,
            string workingDirectory = null,
            Action<string> onOutputLine = null)
        {
            RunResult result = new RunResult();
            string commandType = "unknown";
            try
            {
                commandType = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
            }
            catch (Exception)
            {
                commandType = fileName ?? "command";
            }
            result.CommandType = commandType;

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = fileName;
            startInfo.Arguments = arguments ?? string.Empty;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            StringBuilder stdoutBuilder = new StringBuilder();
            StringBuilder stderrBuilder = new StringBuilder();

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        lock (stdoutBuilder)
                        {
                            stdoutBuilder.AppendLine(e.Data);
                        }
                        if (onOutputLine != null)
                        {
                            try { onOutputLine(e.Data); } catch (Exception) { }
                        }
                    }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        lock (stderrBuilder)
                        {
                            stderrBuilder.AppendLine(e.Data);
                        }
                    }
                };

                try
                {
                    if (!process.Start())
                    {
                        result.Success = false;
                        result.ErrorMessage = "启动 " + commandType + " 失败（进程无法创建）";
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorMessage = "启动 " + commandType + " 异常：" + ex.GetType().Name + ": " + ex.Message;
                    return result;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool finished = process.WaitForExit(timeoutMilliseconds);
                if (!finished)
                {
                    result.TimedOut = true;
                    result.Success = false;
                    KillProcessTree(process);
                    result.ErrorMessage = commandType + " 执行超时（超过 " + (timeoutMilliseconds / 1000) + " 秒）并已被强制终止";
                    return result;
                }

                // 确保异步流读取完毕
                process.WaitForExit();

                result.ExitCode = process.ExitCode;
                lock (stdoutBuilder)
                {
                    result.StandardOutput = stdoutBuilder.ToString();
                }
                lock (stderrBuilder)
                {
                    result.StandardError = stderrBuilder.ToString();
                }

                // 退出码语义判定
                if (commandType == "robocopy")
                {
                    // robocopy 位掩码退出码：0-7 属于不同程度的复制成功，8+ 为失败
                    if (result.ExitCode >= 0 && result.ExitCode <= 7)
                    {
                        result.Success = true;
                    }
                    else
                    {
                        result.Success = false;
                        string combined = (result.StandardOutput ?? "") + " " + (result.StandardError ?? "");
                        bool isSharingViolation = combined.IndexOf("ERROR 32", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                  combined.IndexOf("ERROR 33", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                  combined.IndexOf("used by another process", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                  combined.IndexOf("SHARING_VIOLATION", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                  combined.IndexOf("LOCK_VIOLATION", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (isSharingViolation)
                        {
                            result.ErrorMessage = "文件被其他进程占用（检测到共享锁定冲突，robocopy 退出码 " + result.ExitCode + "）";
                        }
                        else
                        {
                            result.ErrorMessage = "robocopy 执行失败，退出码 " + result.ExitCode +
                                (!string.IsNullOrEmpty(result.StandardError) ? "：" + result.StandardError.Trim() : "");
                        }
                    }
                }
                else
                {
                    result.Success = (result.ExitCode == 0);
                    if (!result.Success)
                    {
                        string errDetail = !string.IsNullOrEmpty(result.StandardError)
                            ? result.StandardError.Trim()
                            : (!string.IsNullOrEmpty(result.StandardOutput) ? result.StandardOutput.Trim() : string.Empty);

                        if (commandType == "cipher")
                        {
                            result.ErrorMessage = "cipher 加密工具执行失败，退出码 " + result.ExitCode +
                                (!string.IsNullOrEmpty(errDetail) ? "：" + errDetail : "");
                        }
                        else
                        {
                            result.ErrorMessage = commandType + " 执行失败，退出码 " + result.ExitCode +
                                (!string.IsNullOrEmpty(errDetail) ? "：" + errDetail : "");
                        }
                    }
                }

                return result;
            }
        }

        private static void KillProcessTree(Process process)
        {
            if (process == null) return;
            try
            {
                int pid = process.Id;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/F /T /PID " + pid)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (Process killer = Process.Start(psi))
                    {
                        if (killer != null)
                        {
                            killer.WaitForExit(3000);
                        }
                    }
                }
                catch (Exception) { }

                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (Exception) { }

                process.WaitForExit(3000);
            }
            catch (Exception) { }
        }
    }
}
