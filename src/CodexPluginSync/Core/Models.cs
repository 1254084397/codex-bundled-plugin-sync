using System.Collections.Generic;

namespace CodexPluginSync.Core
{
    // §6.1 StateSnapshot. Field order here has no meaning; SnapshotWriter owns the JSON order.
    public sealed class PluginRef
    {
        public string Name;
        public string Version;
    }

    public sealed class DesktopInfo
    {
        public bool Found;
        public string Version;
        public string PackageFullName;
    }

    public sealed class MarketplaceInfo
    {
        public bool Exists;
        public bool? NeedsUpdate;
        public List<PluginRef> DesktopPlugins = new List<PluginRef>();
        public List<PluginRef> LocalPlugins = new List<PluginRef>();
        public List<PluginRef> Materialized = new List<PluginRef>();
    }

    public sealed class ComponentInfo
    {
        public string Type;
        public string Name;
        public string Expected;
        public string Actual;
        public string State;
    }

    public sealed class NativeHostEntry
    {
        public string State = "unknown";
        public bool? Registered;
        public string ManifestPath;
        public string TargetPath;
        /// <summary>Path currently recorded in the manifest (probe result, not part of §6.1).</summary>
        public string CurrentExecutable;
        public string Detail;
    }

    public sealed class NativeHostInfo
    {
        public NativeHostEntry Chrome = new NativeHostEntry();
        public NativeHostEntry Edge = new NativeHostEntry();
    }

    public sealed class EfsInfo
    {
        public bool? StagingParentEncrypted;
        public bool? EncryptionSupported;
    }

    public sealed class LockingProcess
    {
        public string Name;
        public int Pid;
        public bool HasWindow;
    }

    public sealed class FileLockInfo
    {
        public string Path;
        public string Component;
    }

    public sealed class DiskInfo
    {
        public string Drive;
        public long FreeBytes;
    }

    public sealed class BackupRef
    {
        public string BackupId;
        public string Status;
    }

    public sealed class BackupsInfo
    {
        public int Count;
        public BackupRef Newest;
    }

    public sealed class JournalInfo
    {
        public string OperationId;
        public long Seq;
        public string Phase;
        public string UpdatedUtc;
        public string BackupId;
        public int TargetCount;
    }

    public sealed class EnvironmentInfo
    {
        public string OsBuild;
        public long? DotnetRelease;
        public bool IsAdmin;
    }

    public sealed class Snapshot
    {
        public string TimestampUtc;
        public string CodexHome;
        public DesktopInfo Desktop = new DesktopInfo();
        public MarketplaceInfo Marketplace = new MarketplaceInfo();
        public List<ComponentInfo> Components = new List<ComponentInfo>();
        public NativeHostInfo NativeHost = new NativeHostInfo();
        public EfsInfo Efs = new EfsInfo();
        public List<LockingProcess> LockingProcesses = new List<LockingProcess>();
        public List<FileLockInfo> FileLocks = new List<FileLockInfo>();
        public List<DiskInfo> Disk = new List<DiskInfo>();
        public BackupsInfo Backups = new BackupsInfo();
        public JournalInfo Journal;
        public EnvironmentInfo Environment = new EnvironmentInfo();
        public string Verdict = Verdicts.Error;
    }

    public static class Verdicts
    {
        public const string Ok = "ok";
        public const string UpdateNeeded = "update-needed";
        public const string Blocked = "blocked";
        public const string Error = "error";
    }

    public static class ComponentStates
    {
        public const string Ok = "ok";
        public const string Outdated = "outdated";
        public const string Missing = "missing";
        public const string Unknown = "unknown";
    }
}
