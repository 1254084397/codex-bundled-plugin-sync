using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace CodexPluginSync.Core
{
    public sealed class CheckOptions
    {
        public string CodexHome;
        public string SourceBundleOverride;
        public int MinimumFreeGB = 1;
        public List<string> ExtraLockingProcesses;
    }

    public sealed class CheckOutcome
    {
        public Snapshot Snapshot = new Snapshot();
        public int ExitCode;
        public string ErrorCode;
        public string ErrorMessage;
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// §7.1 read-only check. Produces the StateSnapshot that the CLI, the JSON layer and (M3)
    /// the UI all consume. Hard rule: this code path never writes inside CodexHome.
    /// </summary>
    public sealed class CheckEngine
    {
        private readonly CheckOptions _options;
        private readonly Logger _log;

        public CheckEngine(CheckOptions options, Logger log)
        {
            _options = options;
            _log = log;
        }

        public CheckOutcome Execute()
        {
            CheckOutcome outcome = new CheckOutcome();
            Snapshot snapshot = outcome.Snapshot;
            CodexPaths paths = new CodexPaths(_options.CodexHome);

            snapshot.TimestampUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            snapshot.CodexHome = paths.CodexHome;
            snapshot.Environment = DescribeEnvironment();
            if (_log != null)
            {
                _log.Info("check", "codexHome=" + paths.CodexHome + " osBuild=" + snapshot.Environment.OsBuild);
            }

            string sourceBundle = ResolveSourceBundle(snapshot);
            if (snapshot.Desktop.Found && string.IsNullOrEmpty(_options.SourceBundleOverride) && sourceBundle == null)
            {
                Fail(outcome, "source-bundle-unresolved",
                    "Desktop package found but its bundled-plugin path could not be derived.");
                return outcome;
            }
            if (!snapshot.Desktop.Found)
            {
                Fail(outcome, "desktop-not-found",
                    "OpenAI.Codex is not installed for this user. Install Codex Desktop from the Microsoft Store.");
                FillEnvironmentContext(snapshot, paths);
                return outcome;
            }

            if (!Directory.Exists(paths.CodexHome))
            {
                Fail(outcome, "codex-home-missing", "CodexHome does not exist: " + paths.CodexHome);
                return outcome;
            }
            if (string.IsNullOrEmpty(sourceBundle) || !Directory.Exists(sourceBundle))
            {
                Fail(outcome, "source-bundle-missing",
                    "Bundled plugin source not found: " + (sourceBundle ?? "(null)"));
                FillEnvironmentContext(snapshot, paths);
                return outcome;
            }
            if (!Directory.Exists(paths.MarketParent))
            {
                Fail(outcome, "market-parent-missing", "Marketplace parent directory is missing: " + paths.MarketParent);
                FillEnvironmentContext(snapshot, paths);
                return outcome;
            }

            CollectMarketplace(snapshot, paths, sourceBundle);
            CollectComponents(snapshot, paths);
            CollectNativeHost(snapshot, paths);
            FillEnvironmentContext(snapshot, paths);
            ClassifyVerdict(outcome, paths);

            if (_log != null)
            {
                _log.Info("check", "verdict=" + snapshot.Verdict + " exitCode=" + outcome.ExitCode.ToString(CultureInfo.InvariantCulture));
            }
            return outcome;
        }

        private string ResolveSourceBundle(Snapshot snapshot)
        {
            if (!string.IsNullOrEmpty(_options.SourceBundleOverride))
            {
                snapshot.Desktop.Found = Directory.Exists(_options.SourceBundleOverride);
                snapshot.Desktop.Version = null;
                snapshot.Desktop.PackageFullName = null;
                return _options.SourceBundleOverride;
            }
            DesktopPackage package = AppxLocator.Locate();
            snapshot.Desktop.Found = package.Found;
            snapshot.Desktop.Version = package.Version;
            snapshot.Desktop.PackageFullName = package.PackageFullName;
            if (!package.Found)
            {
                return null;
            }
            return package.SourceBundle;
        }

        private void CollectMarketplace(Snapshot snapshot, CodexPaths paths, string sourceBundle)
        {
            MarketplaceInfo marketplace = snapshot.Marketplace;
            marketplace.Exists = Directory.Exists(paths.MarketBundle);
            marketplace.DesktopPlugins = PluginInventory.ReadBundle(sourceBundle);
            marketplace.LocalPlugins = marketplace.Exists
                ? PluginInventory.ReadBundle(paths.MarketBundle)
                : new List<PluginRef>();

            string diagnostics;
            marketplace.Materialized = PluginInventory.ReadExpectedPlugins(
                sourceBundle, paths.MarketBundle, marketplace.DesktopPlugins, out diagnostics);
            if (_log != null && diagnostics != null)
            {
                _log.Info("check", diagnostics.Replace(' ', '-'));
            }
            marketplace.NeedsUpdate = !PluginInventory.SameSet(marketplace.Materialized, marketplace.LocalPlugins);
        }

        private void CollectComponents(Snapshot snapshot, CodexPaths paths)
        {
            List<PluginRef> expected = snapshot.Marketplace.Materialized;
            for (int i = 0; i < PluginInventory.ManagedPlugins.Length; i++)
            {
                string name = PluginInventory.ManagedPlugins[i];
                ComponentInfo component = new ComponentInfo();
                component.Type = "cache";
                component.Name = name;
                component.Expected = FindVersion(expected, name);
                component.Actual = PluginInventory.ReadCacheVersion(paths.CacheParent, name);

                if (component.Actual == null)
                {
                    component.State = ComponentStates.Missing;
                }
                else if (component.Expected == null)
                {
                    component.State = ComponentStates.Unknown;
                }
                else
                {
                    component.State = string.Equals(component.Expected, component.Actual, StringComparison.OrdinalIgnoreCase)
                        ? ComponentStates.Ok
                        : ComponentStates.Outdated;
                }
                snapshot.Components.Add(component);
            }
        }

        private void CollectNativeHost(Snapshot snapshot, CodexPaths paths)
        {
            string chromeVersion = FindVersion(snapshot.Marketplace.Materialized, "chrome");
            string expectedExecutable = paths.ExpectedChromeHostExecutable(chromeVersion);
            snapshot.NativeHost.Chrome = NativeHostProbe.ProbeChrome(expectedExecutable);

            NativeHostEntry edge = new NativeHostEntry();
            edge.State = "not-checked";
            edge.Registered = null;
            snapshot.NativeHost.Edge = edge;
        }

        private void FillEnvironmentContext(Snapshot snapshot, CodexPaths paths)
        {
            snapshot.LockingProcesses = ProcessGuard.Detect(_options.ExtraLockingProcesses);
            snapshot.FileLocks = new List<FileLockInfo>();
            snapshot.Disk = CollectDisk(paths.CodexHome);
            snapshot.Backups = BackupStore.Enumerate(paths.BackupsRoot);

            bool corrupt;
            snapshot.Journal = OperationJournal.TryRead(paths, out corrupt);
            if (corrupt && _log != null)
            {
                _log.Warn("check", "journal=corrupt");
            }

            // §7.1/§8.1 (v0.3): --check 只读属性，不执行 EFS 能力探测（探测需要建临时文件）。
            snapshot.Efs.StagingParentEncrypted = EfsProbe.IsEncrypted(paths.MarketParent);
            snapshot.Efs.EncryptionSupported = null;
        }

        private List<DiskInfo> CollectDisk(string codexHome)
        {
            List<DiskInfo> disks = new List<DiskInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddDisk(disks, seen, codexHome);
            AddDisk(disks, seen, _options.SourceBundleOverride);
            return disks;
        }

        private static void AddDisk(List<DiskInfo> disks, HashSet<string> seen, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || seen.Contains(root))
                {
                    return;
                }
                DriveInfo drive = new DriveInfo(root);
                DiskInfo info = new DiskInfo();
                info.Drive = root;
                info.FreeBytes = drive.AvailableFreeSpace;
                disks.Add(info);
                seen.Add(root);
            }
            catch (Exception)
            {
            }
        }

        private void ClassifyVerdict(CheckOutcome outcome, CodexPaths paths)
        {
            Snapshot snapshot = outcome.Snapshot;
            bool updateNeeded = snapshot.Marketplace.NeedsUpdate == true;

            for (int i = 0; i < snapshot.Components.Count; i++)
            {
                ComponentInfo component = snapshot.Components[i];
                if (component.Expected == null)
                {
                    continue;
                }
                if (!string.Equals(component.State, ComponentStates.Ok, StringComparison.Ordinal))
                {
                    updateNeeded = true;
                }
            }

            string chromeState = snapshot.NativeHost.Chrome.State;
            if (string.Equals(chromeState, "outdated", StringComparison.Ordinal) ||
                string.Equals(chromeState, "stale", StringComparison.Ordinal))
            {
                updateNeeded = true;
            }

            bool blocked = false;
            if (snapshot.Journal != null)
            {
                blocked = true;
                outcome.Warnings.Add("pending-journal:" + snapshot.Journal.Phase);
            }
            if (string.Equals(chromeState, "corrupt", StringComparison.Ordinal) ||
                string.Equals(chromeState, "no-permission", StringComparison.Ordinal))
            {
                blocked = true;
                outcome.Warnings.Add("native-host-chrome:" + snapshot.NativeHost.Chrome.Detail);
            }

            long minimumBytes = (long)_options.MinimumFreeGB * 1024L * 1024L * 1024L;
            for (int i = 0; i < snapshot.Disk.Count; i++)
            {
                if (snapshot.Disk[i].FreeBytes < minimumBytes)
                {
                    blocked = true;
                    outcome.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                        "insufficient-free-space:{0}", snapshot.Disk[i].Drive));
                }
            }

            if (blocked)
            {
                snapshot.Verdict = Verdicts.Blocked;
                outcome.ExitCode = Cli.ExitCodes.Error;
            }
            else if (updateNeeded)
            {
                snapshot.Verdict = Verdicts.UpdateNeeded;
                outcome.ExitCode = Cli.ExitCodes.NeedsUpdate;
            }
            else
            {
                snapshot.Verdict = Verdicts.Ok;
                outcome.ExitCode = Cli.ExitCodes.Ok;
            }

            if (_log != null)
            {
                for (int i = 0; i < outcome.Warnings.Count; i++)
                {
                    _log.Warn("check", outcome.Warnings[i]);
                }
            }
        }

        private void Fail(CheckOutcome outcome, string code, string message)
        {
            outcome.Snapshot.Verdict = Verdicts.Error;
            outcome.ErrorCode = code;
            outcome.ErrorMessage = message;
            outcome.ExitCode = Cli.ExitCodes.Error;
            if (_log != null)
            {
                _log.Error("check", "code=" + code);
            }
        }

        private static string FindVersion(List<PluginRef> plugins, string name)
        {
            for (int i = 0; i < plugins.Count; i++)
            {
                if (string.Equals(plugins[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return plugins[i].Version;
                }
            }
            return null;
        }

        private static EnvironmentInfo DescribeEnvironment()
        {
            EnvironmentInfo info = new EnvironmentInfo();
            info.OsBuild = ReadString(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber");
            info.DotnetRelease = ReadLong(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release");
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                info.IsAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                info.IsAdmin = false;
            }
            return info;
        }

        private static string ReadString(RegistryKey root, string path, string name)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(path, false))
                {
                    if (key == null)
                    {
                        return null;
                    }
                    object value = key.GetValue(name);
                    return value == null ? null : value.ToString();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static long? ReadLong(RegistryKey root, string path, string name)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(path, false))
                {
                    if (key == null)
                    {
                        return null;
                    }
                    object value = key.GetValue(name);
                    if (value == null)
                    {
                        return null;
                    }
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
