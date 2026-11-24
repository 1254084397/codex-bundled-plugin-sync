using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// 目录复制（对应脚本层 Copy-BundledTree / Copy-CodexSyncReparseLinks）。
    /// 关键点：junction / 符号链接目录不递归展开（否则 `chrome\latest` 会被拷成整份真实目录，
    /// 真机上实测把 158 个文件变成 317 个），复制后按原目标重建，保持与源同构。
    /// </summary>
    public static class Installer
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandleW(IntPtr hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
        private const FileAttributes ReparseFlag = FileAttributes.ReparsePoint;

        public static void CopyTree(string source, string destination, Func<bool> isCancelled = null)
        {
            if (string.IsNullOrEmpty(source)) { throw new ArgumentNullException("source"); }
            if (string.IsNullOrEmpty(destination)) { throw new ArgumentNullException("destination"); }
            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException("源目录不存在：" + source);
            }

            CopyDirectory(source, destination, new List<ReparseEntry>(), isCancelled);
        }

        private sealed class ReparseEntry
        {
            public string RelativePath;
            public string Target;
        }

        private static void CopyDirectory(string source, string destination, List<ReparseEntry> links, Func<bool> isCancelled = null)
        {
            if (isCancelled != null && isCancelled())
            {
                throw new OperationCanceledException("复制操作已被取消");
            }

            Directory.CreateDirectory(destination);

            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(source);
                directories = Directory.GetDirectories(source);
            }
            catch (Exception ex)
            {
                throw new IOException("枚举目录失败：" + source + " —— " + ex.Message, ex);
            }

            for (int i = 0; i < files.Length; i++)
            {
                if (isCancelled != null && isCancelled())
                {
                    throw new OperationCanceledException("复制操作已被取消");
                }
                string target = Path.Combine(destination, Path.GetFileName(files[i]));
                CopyFileRobust(files[i], target, destination);
            }

            for (int i = 0; i < directories.Length; i++)
            {
                if (isCancelled != null && isCancelled())
                {
                    throw new OperationCanceledException("复制操作已被取消");
                }
                string directory = directories[i];
                string name = Path.GetFileName(directory);
                string target = Path.Combine(destination, name);
                FileAttributes attributes = File.GetAttributes(directory);
                if ((attributes & ReparseFlag) == ReparseFlag)
                {
                    // 记录后重建；不在这里递归
                    ReparseEntry entry = new ReparseEntry();
                    entry.RelativePath = target;
                    entry.Target = ResolveLinkTarget(directory);
                    links.Add(entry);
                    continue;
                }
                CopyDirectory(directory, target, links, isCancelled);
            }
        }

        /// <summary>
        /// 复制单个文件。真机验收（2026-11-24）撞到的坑：AppX 源包里的文件本身带 EFS 加密属性，
        /// 把它 File.Copy 到**未加密**的目标目录会直接失败（ERROR_ENCRYPTION_FAILED 6000，
        /// “无法加密指定的文件”），而复制到加密目录反而正常。这里分两条路：
        ///   - 源加密 + 目标是未加密目录 → 字节流复制（目标不带 EFS 属性，这正是想要的结果）；
        ///   - 其余情况仍用 File.Copy，保留时间戳与属性。
        /// 另外保留了 6000 的异常回退，避免未预料的继承场景又炸一次。
        /// </summary>
        private static void CopyFileRobust(string source, string destination, string destinationDirectory)
        {
            bool sourceEncrypted = IsEncrypted(source);
            bool destinationEncrypted = IsEncrypted(destinationDirectory);

            if (sourceEncrypted && !destinationEncrypted)
            {
                CopyByStream(source, destination);
                return;
            }

            try
            {
                File.Copy(source, destination, true);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                CopyByStream(source, destination);
            }
            catch (IOException ex)
            {
                // 0x1770 = ERROR_ENCRYPTION_FAILED
                if ((ex.HResult & 0xFFFF) != 0x1770 && ex.HResult != unchecked((int)0x80071770))
                {
                    throw;
                }
                CopyByStream(source, destination);
            }
        }

        private static bool IsEncrypted(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.Encrypted) == FileAttributes.Encrypted;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void CopyByStream(string source, string destination)
        {
            string parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }

            DateTime lastWrite = File.GetLastWriteTimeUtc(source);
            DateTime creation = File.GetCreationTimeUtc(source);

            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                }
            }

            try
            {
                File.SetLastWriteTimeUtc(destination, lastWrite);
                File.SetCreationTimeUtc(destination, creation);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 复制完成后再统一重建 reparse point：目标仍在源树内时映射到副本内，
        /// 否则按原目标重建（脚本层同样处理）。
        /// </summary>
        public static void CopyTreePreservingLinks(string source, string destination, Func<bool> isCancelled = null)
        {
            List<ReparseEntry> links = new List<ReparseEntry>();
            CopyDirectory(source, destination, links, isCancelled);

            string sourceRoot = Path.GetFullPath(source).TrimEnd('\\');
            string destinationRoot = Path.GetFullPath(destination).TrimEnd('\\');

            for (int i = 0; i < links.Count; i++)
            {
                ReparseEntry entry = links[i];
                string linkTarget = entry.Target;
                if (string.IsNullOrEmpty(linkTarget)) { continue; }

                if (linkTarget.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string relative = linkTarget.Substring(sourceRoot.Length).TrimStart('\\');
                    linkTarget = Path.Combine(destinationRoot, relative);
                }

                if (Directory.Exists(entry.RelativePath) || File.Exists(entry.RelativePath)) { continue; }
                try
                {
                    CreateJunction(entry.RelativePath, linkTarget);
                }
                catch (Exception ex)
                {
                    throw new IOException("重建 junction 失败：" + entry.RelativePath + " —— " + ex.Message, ex);
                }
            }
        }

        /// <summary>
        /// 解析 junction 的真实目标。net48 没有 DirectoryInfo.LinkTarget，
        /// 这里用 GetFinalPathNameByHandle（与脚本层同一手段）。
        /// </summary>
        private static string ResolveLinkTarget(string path)
        {
            IntPtr handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.ToInt64() == -1 || handle == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                StringBuilder buffer = new StringBuilder(1024);
                uint length = GetFinalPathNameByHandleW(handle, buffer, 1024, 0);
                if (length == 0)
                {
                    return null;
                }
                string final = buffer.ToString();
                if (final.StartsWith(@"\\?\")) { final = final.Substring(4); }
                return final;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static void CreateJunction(string path, string target)
        {
            string parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }

            // 用 mklink /J 建：目标暂时不存在时（回滚场景）也能建悬挂链接，
            // 而 .NET API 在目标缺失时会直接拒绝。
            ProcessRunner.RunResult run = ProcessRunner.Run("cmd.exe", "/c mklink /J \"" + path + "\" \"" + target + "\"", 20000);
            if (!run.Success || !Directory.Exists(path))
            {
                string detail = !string.IsNullOrEmpty(run.StandardError) ? run.StandardError.Trim() : run.StandardOutput.Trim();
                throw new IOException("mklink /J 失败：" + path + " -> " + target + (string.IsNullOrEmpty(detail) ? "" : "（" + detail + "）"));
            }
        }
    }
}
