using System;
using System.Diagnostics;
using System.IO;

namespace CodexPluginSync.Core
{
    public sealed class EfsState
    {
        public string Path;
        public bool? PreEncrypted;
        public bool EnabledByThisRun;
    }

    public sealed class EfsRestoreResult
    {
        public bool Restored;
        public bool? PreEncrypted;
        public bool? NowEncrypted;
        public string Message;
    }

    /// <summary>
    /// §8 EFS 契约：只记录、只撤销本次自己的开启。
    /// 成功判据是“重新读到的属性值”，不是 cipher 的退出码——
    /// 实测 `cipher /d` 对不存在的路径仍返回 0，读不到属性就当失败（needs-attention）。
    /// </summary>
    public static class Efs
    {
        public static bool? GetEncrypted(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { return null; }
                FileAttributes attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.Encrypted) == FileAttributes.Encrypted;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool SetEncrypted(string path, bool encrypted)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = "cipher";
            startInfo.Arguments = (encrypted ? "/e " : "/d ") + "\"" + path + "\"";
            startInfo.CreateNoWindow = true;
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            using (Process process = Process.Start(startInfo))
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) { return false; }
            }
            bool? now = GetEncrypted(path);
            return now.HasValue && now.Value == encrypted;
        }

        /// <summary>
        /// 为写入 Application Protected 源做准备：记录 preEncrypted；未加密则开启并记 enabledByThisRun。
        /// 开启失败不静默：抛异常，由调用方在动 live 之前终止。
        /// </summary>
        public static EfsState PrepareForWrite(string path)
        {
            EfsState state = new EfsState();
            state.Path = path;
            state.PreEncrypted = GetEncrypted(path);
            if (!state.PreEncrypted.HasValue)
            {
                throw new InvalidOperationException("无法读取 " + path + " 的加密属性，EFS 预检失败");
            }
            if (!state.PreEncrypted.Value)
            {
                if (!SetEncrypted(path, true))
                {
                    throw new InvalidOperationException("开启 EFS 失败：" + path + "（cipher /e 未生效）");
                }
                state.EnabledByThisRun = true;
            }
            return state;
        }

        /// <summary>
        /// 探测本卷是否支持 EFS：在目标目录下建一个临时子目录试开启，用完删掉。
        /// 用于“别人的机器不支持 EFS”时优雅降级——不把环境限制当成同步失败。
        /// </summary>
        public static bool SupportsEfs(string path)
        {
            if (string.IsNullOrEmpty(path)) { return false; }
            string probe = Path.Combine(path, ".efs-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                if (!Directory.Exists(path)) { Directory.CreateDirectory(path); }
                Directory.CreateDirectory(probe);
                if (!SetEncrypted(probe, true)) { return false; }
                SetEncrypted(probe, false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                try { if (Directory.Exists(probe)) { SetEncrypted(probe, false); Directory.Delete(probe, true); } }
                catch (Exception) { }
            }
        }

        /// <summary>收尾：只撤销本次自己的开启；读不到属性即视为未恢复。</summary>
        public static EfsRestoreResult Restore(EfsState state)
        {
            EfsRestoreResult result = new EfsRestoreResult();
            if (state == null)
            {
                result.Restored = true;
                return result;
            }
            result.PreEncrypted = state.PreEncrypted;

            if (!state.EnabledByThisRun)
            {
                result.NowEncrypted = GetEncrypted(state.Path);
                if (!result.NowEncrypted.HasValue)
                {
                    result.Restored = false;
                    result.Message = "无法确认 EFS 状态（读不到加密属性），不宣称已恢复";
                    return result;
                }
                if (state.PreEncrypted.HasValue && result.NowEncrypted.Value != state.PreEncrypted.Value)
                {
                    result.Restored = false;
                    result.Message = "EFS 状态与本次进入时不同（原=" + state.PreEncrypted + " 现=" + result.NowEncrypted + "），不是本次改的";
                    return result;
                }
                result.Restored = true;
                return result;
            }

            bool disabled = SetEncrypted(state.Path, state.PreEncrypted.HasValue && state.PreEncrypted.Value);
            result.NowEncrypted = GetEncrypted(state.Path);
            if (!result.NowEncrypted.HasValue)
            {
                result.Restored = false;
                result.Message = "撤销后读不到加密属性，无法确认已恢复";
                return result;
            }
            if (state.PreEncrypted.HasValue && result.NowEncrypted.Value != state.PreEncrypted.Value)
            {
                result.Restored = false;
                result.Message = "撤销后加密状态仍不匹配（原=" + state.PreEncrypted + " 现=" + result.NowEncrypted + "）";
                return result;
            }
            result.Restored = disabled;
            if (!disabled) { result.Message = "cipher 未成功，已按未恢复处理"; }
            return result;
        }
    }
}
