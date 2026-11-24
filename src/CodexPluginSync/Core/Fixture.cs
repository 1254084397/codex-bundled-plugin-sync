using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CodexPluginSync.Core;

namespace CodexPluginSync.Tests
{
    /// <summary>
    /// Builds the fake CodexHome + fake bundled-plugin source described in §16.1.
    /// The AppX side is injected through --source-bundle, so these fixtures need no Codex install.
    /// </summary>
    public sealed class Fixture : IDisposable
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public string Root { get; set; }
        public string CodexHome { get; set; }
        public string SourceBundle { get; set; }

        public string MarketParent
        {
            get { return Path.Combine(CodexHome, @".tmp\bundled-marketplaces"); }
        }

        public string MarketBundle
        {
            get { return Path.Combine(MarketParent, "openai-bundled"); }
        }

        public string CacheParent
        {
            get { return Path.Combine(CodexHome, @"plugins\cache\openai-bundled"); }
        }

        public Fixture(string baseDirectory, string name)
        {
            Root = Path.Combine(baseDirectory, name);
            CodexHome = Path.Combine(Root, "CodexHome");
            SourceBundle = Path.Combine(Root, "bundle");
            Directory.CreateDirectory(CodexHome);
            Directory.CreateDirectory(SourceBundle);
            Directory.CreateDirectory(MarketParent);
        }

        public void AddDesktopPlugin(string pluginName, string version)
        {
            WriteManifest(Path.Combine(Path.Combine(SourceBundle, "plugins"), pluginName), pluginName, version);
        }

        public void AddLocalPlugin(string pluginName, string version)
        {
            Directory.CreateDirectory(MarketBundle);
            WriteManifest(Path.Combine(Path.Combine(MarketBundle, "plugins"), pluginName), pluginName, version);
        }

        public void AddCache(string pluginName, string version)
        {
            WriteManifest(Path.Combine(Path.Combine(CacheParent, pluginName), version), pluginName, version);
        }

        public void SetMaterializationKey(params string[] pluginNames)
        {
            Directory.CreateDirectory(MarketBundle);
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"plugins\":[");
            for (int i = 0; i < pluginNames.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append("{\"name\":\"").Append(pluginNames[i]).Append("\"}");
            }
            sb.Append("]}");
            File.WriteAllText(Path.Combine(MarketBundle, ".materialization-key"), sb.ToString(), Utf8NoBom);
        }

        public void SetBundleMarketplaceManifest(params string[] pluginNames)
        {
            string directory = Path.Combine(Path.Combine(SourceBundle, ".agents"), "plugins");
            Directory.CreateDirectory(directory);
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"plugins\":[");
            for (int i = 0; i < pluginNames.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append("{\"name\":\"").Append(pluginNames[i]).Append("\"}");
            }
            sb.Append("]}");
            File.WriteAllText(Path.Combine(directory, "marketplace.json"), sb.ToString(), Utf8NoBom);
        }

        public void WriteJournal(string phase)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"schemaVersion\":1,\"operationId\":\"20260612-143200\",\"seq\":7,");
            sb.Append("\"updatedUtc\":\"2026-06-12T06:32:07Z\",\"phase\":\"").Append(phase).Append("\",");
            sb.Append("\"backupId\":\"20260612-143200\",\"appxVersion\":\"26.1.2\",");
            sb.Append("\"targets\":[{\"component\":\"marketplace\",\"stagePath\":\"s\",\"livePath\":\"l\",");
            sb.Append("\"swappedOut\":true,\"swappedIn\":false}]}");
            File.WriteAllText(Path.Combine(CodexHome, "pending-operation.json"), sb.ToString(), Utf8NoBom);
        }

        public void WriteBackup(string backupId, string status)
        {
            string directory = Path.Combine(Path.Combine(CodexHome, "backups"), backupId);
            Directory.CreateDirectory(directory);
            string json = "{\"schemaVersion\":1,\"backupId\":\"" + backupId + "\",\"kind\":\"sync\",\"status\":\"" +
                status + "\",\"createdUtc\":\"2026-06-12T06:32:00Z\",\"components\":[]}";
            File.WriteAllText(Path.Combine(directory, "backup.json"), json, Utf8NoBom);
        }

        /// <summary>Creates a directory junction (mklink /J) — needs no elevation on NTFS.</summary>
        public static bool TryCreateJunction(string linkPath, string targetPath)
        {
            try
            {
                ProcessRunner.RunResult run = ProcessRunner.Run("cmd.exe", "/c mklink /J \"" + linkPath + "\" \"" + targetPath + "\"", 20000);
                return run.Success && Directory.Exists(linkPath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void WriteManifest(string pluginDirectory, string pluginName, string version)
        {
            string directory = Path.Combine(pluginDirectory, ".codex-plugin");
            Directory.CreateDirectory(directory);
            string json = "{\"name\":\"" + pluginName + "\",\"version\":\"" + version + "\"}";
            File.WriteAllText(Path.Combine(directory, "plugin.json"), json, Utf8NoBom);
        }

        /// <summary>Content fingerprint of a directory tree, used to prove read-only behaviour.</summary>
        public static string Fingerprint(string directory)
        {
            List<string> entries = new List<string>();
            if (Directory.Exists(directory))
            {
                string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
                for (int i = 0; i < files.Length; i++)
                {
                    FileInfo info = new FileInfo(files[i]);
                    entries.Add(files[i].Substring(directory.Length) + "|" +
                        info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            entries.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("\n", entries.ToArray());
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, true);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
