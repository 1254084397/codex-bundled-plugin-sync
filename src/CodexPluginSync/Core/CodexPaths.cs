using System;
using System.IO;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// Every CodexHome-relative path the tool touches lives here (§4.2: Core never invents paths).
    /// </summary>
    public sealed class CodexPaths
    {
        public const string MarketplaceFolderName = "openai-bundled";
        public const string ChromeNativeHostRegistryPath = @"Software\Google\Chrome\NativeMessagingHosts\com.openai.codexextension";
        public const string NativeHostId = "com.openai.codexextension";
        public const string BackupFolderPrefix = "openai-bundled-sync-";
        public const string StageFolderPrefix = "openai-bundled.stage-";
        public const string JournalFileName = "pending-operation.json";
        public const string ToolName = "CodexPluginSync";

        private readonly string _codexHome;

        public CodexPaths(string codexHome)
        {
            if (codexHome == null)
            {
                throw new ArgumentNullException("codexHome");
            }
            _codexHome = TrimTrailingSeparators(codexHome);
        }

        public string CodexHome
        {
            get { return _codexHome; }
        }

        public string MarketParent
        {
            get { return Path.Combine(_codexHome, @".tmp\bundled-marketplaces"); }
        }

        public string MarketBundle
        {
            get { return Path.Combine(MarketParent, MarketplaceFolderName); }
        }

        public string CacheParent
        {
            get { return Path.Combine(_codexHome, @"plugins\cache\openai-bundled"); }
        }

        public string BackupsRoot
        {
            get { return Path.Combine(_codexHome, "backups"); }
        }

        public string JournalPath
        {
            get { return Path.Combine(_codexHome, JournalFileName); }
        }

        /// <summary>Log directory used by writing operations (§6.5, legacy path).</summary>
        public string LogDirectory
        {
            get { return Path.Combine(_codexHome, "logs"); }
        }

        public string StagePath(string componentId, string operationId)
        {
            string name = SafeName(componentId);
            return Path.Combine(MarketParent, StageFolderPrefix + name + "-" + operationId);
        }

        public string ExpectedChromeHostExecutable(string chromeVersion)
        {
            if (string.IsNullOrEmpty(chromeVersion))
            {
                return null;
            }
            return Path.Combine(Path.Combine(Path.Combine(Path.Combine(CacheParent, "chrome"), chromeVersion),
                "extension-host"), @"windows\x64\extension-host.exe");
        }

        /// <summary>%LOCALAPPDATA%\CodexPluginSync — settings and read-only-operation logs (§6.2, §7.1).</summary>
        public static string ToolDataDirectory
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, ToolName);
            }
        }

        public static string SettingsPath
        {
            get { return Path.Combine(ToolDataDirectory, "settings.json"); }
        }

        public static string ToolLogDirectory
        {
            get { return Path.Combine(ToolDataDirectory, "logs"); }
        }

        public static string ResolveCodexHome(string explicitHome)
        {
            if (!string.IsNullOrEmpty(explicitHome))
            {
                return TrimTrailingSeparators(explicitHome);
            }
            string env = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrEmpty(env))
            {
                return TrimTrailingSeparators(env);
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CodexHome");
        }

        public static string Stamp()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss");
        }

        public static string TrimTrailingSeparators(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }
            string trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length == 2 && trimmed[1] == ':')
            {
                return trimmed + "\\";
            }
            return trimmed;
        }

        private static string SafeName(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            System.Text.StringBuilder sb = new System.Text.StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                sb.Append(Array.IndexOf(invalid, c) >= 0 || c == ':' ? '-' : c);
            }
            return sb.ToString();
        }
    }
}
