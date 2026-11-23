using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace CodexPluginSync.Core
{
    public sealed class NativeHostResult
    {
        public bool Applied;
        public bool Skipped;
        public bool Drift;
        public string Reason;
        public string ManifestPath;
        public string BackupPath;
        public string PreviousTargetPath;
        public bool RegistryValueExisted;
    }

    /// <summary>
    /// Chrome native-messaging 清单写入（§9.4）。
    /// 注册表默认值是外部数据：只接受落在用户目录内、且文件名恰为 com.openai.codexextension.json 的路径。
    /// 指向比较用 PathIdentity（junction 等价），避免把 ...\chrome\latest\... 误判成漂移再去改一个正确的文件。
    /// </summary>
    public static class NativeHostWriter
    {
        public const string ManifestFileName = "com.openai.codexextension.json";
        public const string ChromeRegistryPath = @"Software\Google\Chrome\NativeMessagingHosts\com.openai.codexextension";

        public static string ReadRegistryDefault()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ChromeRegistryPath, false))
            {
                if (key == null) { return null; }
                object value = key.GetValue(null);
                return value == null ? null : value.ToString();
            }
        }

        /// <summary>返回 null 表示可写；否则返回拒绝原因。</summary>
        public static string ValidateManifestPath(string manifestPath, IList<string> allowedRoots)
        {
            if (string.IsNullOrEmpty(manifestPath))
            {
                return "清单路径为空";
            }
            if (manifestPath.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return "清单路径包含 ..";
            }
            if (!string.Equals(Path.GetFileName(manifestPath), ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                return "清单文件名不是 " + ManifestFileName + "：" + manifestPath;
            }
            string parent = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return "清单父目录不存在：" + manifestPath;
            }

            string resolvedParent = PathIdentity.Resolve(parent);
            for (int i = 0; i < allowedRoots.Count; i++)
            {
                string root = allowedRoots[i];
                if (string.IsNullOrEmpty(root)) { continue; }
                string resolvedRoot = PathIdentity.Resolve(root).TrimEnd('\\');
                if (resolvedParent.Equals(resolvedRoot, StringComparison.OrdinalIgnoreCase) ||
                    resolvedParent.StartsWith(resolvedRoot + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }
            return "清单落在允许目录之外：" + resolvedParent;
        }

        public static IList<string> AllowedRoots(string codexHome)
        {
            List<string> roots = new List<string>();
            AddIfExists(roots, Environment.GetEnvironmentVariable("LOCALAPPDATA"));
            AddIfExists(roots, Environment.GetEnvironmentVariable("APPDATA"));
            AddIfExists(roots, codexHome);
            return roots;
        }

        private static void AddIfExists(List<string> roots, string candidate)
        {
            if (string.IsNullOrEmpty(candidate) || !Directory.Exists(candidate)) { return; }
            roots.Add(candidate.TrimEnd('\\'));
        }

        /// <summary>
        /// 把清单指向 cacheParent\chrome\&lt;version&gt;\extension-host\windows\x64\extension-host.exe。
        /// 漂移才写；写入前先备份原文件到 backupDirectory。
        /// </summary>
        public static NativeHostResult Apply(string codexHome, string cacheParent, string chromeVersion, string backupDirectory)
        {
            NativeHostResult result = new NativeHostResult();

            string expected = Path.Combine(cacheParent, "chrome", chromeVersion,
                Path.Combine("extension-host", Path.Combine("windows", Path.Combine("x64", "extension-host.exe"))));
            if (!File.Exists(expected))
            {
                result.Reason = "找不到当前版本的 Chrome native host：" + expected;
                return result;
            }

            string manifestPath = ReadRegistryDefault();
            if (manifestPath == null)
            {
                result.Skipped = true;
                result.Reason = "Chrome native-messaging 注册表项不存在";
                return result;
            }

            result.RegistryValueExisted = true;
            string boundaryError = ValidateManifestPath(manifestPath, AllowedRoots(codexHome));
            if (boundaryError != null)
            {
                result.Reason = "拒绝写入：" + boundaryError;
                return result;
            }
            result.ManifestPath = manifestPath;

            if (!File.Exists(manifestPath))
            {
                result.Reason = "清单文件不存在：" + manifestPath;
                return result;
            }

            string current;
            try
            {
                current = ReadManifestTarget(manifestPath);
            }
            catch (Exception ex)
            {
                result.Reason = "清单不可读：" + ex.Message;
                return result;
            }
            result.PreviousTargetPath = current;

            if (!string.IsNullOrEmpty(current) && File.Exists(current) && File.Exists(expected) &&
                string.Equals(PathIdentity.Resolve(current), PathIdentity.Resolve(expected), StringComparison.OrdinalIgnoreCase))
            {
                result.Skipped = true;
                result.Reason = "清单已指向当前版本";
                return result;
            }

            result.Drift = true;
            try
            {
                if (!string.IsNullOrEmpty(backupDirectory))
                {
                    Directory.CreateDirectory(backupDirectory);
                    string backupPath = Path.Combine(backupDirectory, ManifestFileName + ".previous");
                    File.Copy(manifestPath, backupPath, true);
                    result.BackupPath = backupPath;
                }

                WriteManifestTarget(manifestPath, expected);

                string verified = ReadManifestTarget(manifestPath);
                if (!string.Equals(PathIdentity.Resolve(verified), PathIdentity.Resolve(expected), StringComparison.OrdinalIgnoreCase))
                {
                    result.Reason = "写入后校验失败";
                    return result;
                }
                result.Applied = true;
                result.Reason = "清单已更新";
            }
            catch (Exception ex)
            {
                result.Reason = "写入失败：" + ex.Message;
            }
            return result;
        }

        private static string ReadManifestTarget(string manifestPath)
        {
            string json = File.ReadAllText(manifestPath);
            int index = json.IndexOf("\"path\"", StringComparison.Ordinal);
            if (index < 0) { return null; }
            int colon = json.IndexOf(':', index);
            if (colon < 0) { return null; }
            int firstQuote = json.IndexOf('"', colon);
            if (firstQuote < 0) { return null; }
            int secondQuote = json.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0) { return null; }
            string raw = json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
            return raw.Replace("\\\\", "\\");
        }

        private static void WriteManifestTarget(string manifestPath, string target)
        {
            string json = File.ReadAllText(manifestPath);
            int index = json.IndexOf("\"path\"", StringComparison.Ordinal);
            if (index < 0) { throw new InvalidOperationException("清单中没有 path 字段"); }
            int colon = json.IndexOf(':', index);
            int firstQuote = json.IndexOf('"', colon);
            int secondQuote = json.IndexOf('"', firstQuote + 1);
            if (colon < 0 || firstQuote < 0 || secondQuote < 0)
            {
                throw new InvalidOperationException("清单 path 字段格式异常");
            }

            string escaped = target.Replace("\\", "\\\\");
            string updated = json.Substring(0, firstQuote + 1) + escaped + json.Substring(secondQuote);
            File.WriteAllText(manifestPath, updated, new System.Text.UTF8Encoding(false));
        }
    }
}
