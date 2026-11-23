using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// §6.3 树摘要：枚举文件 → 每个文件算 SHA-256 → 排序后拼 "relative|size|hash" 再取 SHA-256。
    /// 与脚本层 Get-CodexSyncTreeDigest 同一算法（reparse point 不被递归进去）。
    /// </summary>
    public static class TreeDigest
    {
        public static ComponentDigestDto Compute(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return null;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                return null;
            }

            List<string> lines = new List<string>(files.Length);
            long sizeBytes = 0;
            using (SHA256 sha = SHA256.Create())
            {
                for (int i = 0; i < files.Length; i++)
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
            digest.FileCount = files.Length;
            digest.SizeBytes = sizeBytes;
            digest.TreeHash = treeHash;
            return digest;
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
