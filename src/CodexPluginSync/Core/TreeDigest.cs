using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// §6.3 树摘要：枚举文件 → 每个文件算 SHA-256 → 排序后拼 "relative|size|hash" 再取 SHA-256。
    /// 与脚本层 Get-CodexSyncTreeDigest 同一算法。
    ///
    /// 关键点（真机 2026-11-24 踩过）：**不得跟进 reparse point**。
    /// PowerShell 的 Get-ChildItem -Recurse 默认不跟 junction，而 Directory.GetFiles(AllDirectories)
    /// 在 .NET Framework 下**会**跟，于是 chrome 的 158 个文件被算成 316，备份里的 latest junction
    /// 又指回 live，交换后摘要必然对不上 → 回滚拒绝。这里改为自己走目录并跳过 reparse point。
    /// </summary>
    public static class TreeDigest
    {
        public static ComponentDigestDto Compute(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return null;
            }

            List<string> files = new List<string>();
            try
            {
                CollectFiles(root, root, files);
            }
            catch (Exception)
            {
                return null;
            }

            List<string> lines = new List<string>(files.Count);
            long sizeBytes = 0;
            using (SHA256 sha = SHA256.Create())
            {
                for (int i = 0; i < files.Count; i++)
                {
                    string file = files[i];
                    string hash;
                    long length;
                    try
                    {
                        FileInfo info = new FileInfo(file);
                        length = info.Length;
                        using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            hash = ToHex(sha.ComputeHash(fs));
                        }
                    }
                    catch (Exception)
                    {
                        return null;
                    }

                    string relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    lines.Add(relative + "|" + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + hash);
                    sizeBytes += length;
                }
            }

            lines.Sort(StringComparer.Ordinal);
            byte[] joined = Encoding.UTF8.GetBytes(string.Join("\n", lines.ToArray()));
            string treeHash;
            using (SHA256 sha = SHA256.Create())
            {
                treeHash = ToHex(sha.ComputeHash(joined));
            }

            ComponentDigestDto digest = new ComponentDigestDto();
            digest.FileCount = files.Count;
            digest.SizeBytes = sizeBytes;
            digest.TreeHash = treeHash;
            return digest;
        }

        /// <summary>逐层枚举；遇到 reparse point 目录只跳过，不递归、不计数。</summary>
        private static void CollectFiles(string root, string current, List<string> files)
        {
            string[] entries = Directory.GetFiles(current);
            for (int i = 0; i < entries.Length; i++)
            {
                files.Add(entries[i]);
            }

            string[] directories = Directory.GetDirectories(current);
            for (int i = 0; i < directories.Length; i++)
            {
                string directory = directories[i];
                FileAttributes attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    continue;
                }
                CollectFiles(root, directory, files);
            }
        }

        public static bool Equal(ComponentDigestDto left, ComponentDigestDto right)
        {
            if (left == null || right == null)
            {
                return false;
            }
            return left.FileCount == right.FileCount &&
                   left.SizeBytes == right.SizeBytes &&
                   string.Equals(left.TreeHash, right.TreeHash, StringComparison.OrdinalIgnoreCase);
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
