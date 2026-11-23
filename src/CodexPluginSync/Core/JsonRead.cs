using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace CodexPluginSync.Core
{
    // Read-side DTOs. §4.3: DataContractJsonSerializer (BCL only, no JSON library).
    [DataContract]
    public sealed class PluginManifestDto
    {
        [DataMember(Name = "name")]
        public string Name;

        [DataMember(Name = "version")]
        public string Version;
    }

    [DataContract]
    public sealed class PluginEntryDto
    {
        [DataMember(Name = "name")]
        public string Name;

        [DataMember(Name = "version")]
        public string Version;
    }

    [DataContract]
    public sealed class PluginListDto
    {
        [DataMember(Name = "plugins")]
        public List<PluginEntryDto> Plugins;
    }

    [DataContract]
    public sealed class JournalDto
    {
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion;

        [DataMember(Name = "operationId")]
        public string OperationId;

        [DataMember(Name = "seq")]
        public long Seq;

        [DataMember(Name = "updatedUtc")]
        public string UpdatedUtc;

        [DataMember(Name = "phase")]
        public string Phase;

        [DataMember(Name = "backupId")]
        public string BackupId;

        [DataMember(Name = "appxVersion")]
        public string AppxVersion;

        [DataMember(Name = "targets")]
        public List<JournalTargetDto> Targets;
    }

    [DataContract]
    public sealed class JournalTargetDto
    {
        [DataMember(Name = "component")]
        public string Component;

        [DataMember(Name = "stagePath")]
        public string StagePath;

        [DataMember(Name = "livePath")]
        public string LivePath;

        [DataMember(Name = "backupPath")]
        public string BackupPath;

        [DataMember(Name = "existedBefore")]
        public bool ExistedBefore;

        [DataMember(Name = "swappedOut")]
        public bool SwappedOut;

        [DataMember(Name = "swappedIn")]
        public bool SwappedIn;

        /// <summary>回滚专用（§7.3）：矩阵里的第三项由安全快照路径代替 stage。</summary>
        [DataMember(Name = "safetyPath")]
        public string SafetyPath;

        /// <summary>操作前内容摘要（sync：live 同步前；rollback：安全快照内容）。</summary>
        [DataMember(Name = "oldDigest")]
        public ComponentDigestDto OldDigest;

        /// <summary>本次操作要落地的内容摘要（sync：stage；rollback：备份内容）。</summary>
        [DataMember(Name = "newDigest")]
        public ComponentDigestDto NewDigest;
    }

    [DataContract]
    public sealed class BackupManifestDto
    {
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion;

        [DataMember(Name = "backupId")]
        public string BackupId;

        [DataMember(Name = "kind")]
        public string Kind;

        [DataMember(Name = "status")]
        public string Status;

        [DataMember(Name = "createdUtc")]
        public string CreatedUtc;

        [DataMember(Name = "appxVersion")]
        public string AppxVersion;

        [DataMember(Name = "components")]
        public List<BackupComponentDto> Components;

        [DataMember(Name = "nativeHost")]
        public List<BackupNativeHostDto> NativeHost;
    }

    [DataContract]
    public sealed class SettingsDto
    {
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion;

        [DataMember(Name = "codexHome")]
        public string CodexHome;

        [DataMember(Name = "language")]
        public string Language;

        [DataMember(Name = "theme")]
        public string Theme;

        [DataMember(Name = "runAtStartup")]
        public bool RunAtStartup;

        [DataMember(Name = "autoCheckHours")]
        public int AutoCheckHours;

        [DataMember(Name = "keepBackups")]
        public int KeepBackups;

        [DataMember(Name = "staleStageHours")]
        public int StaleStageHours;

        [DataMember(Name = "keepLogDays")]
        public int KeepLogDays;

        [DataMember(Name = "partialBackupRetentionDays")]
        public int PartialBackupRetentionDays;

        [DataMember(Name = "failedBackupRetentionDays")]
        public int FailedBackupRetentionDays;

        [DataMember(Name = "minimumFreeGB")]
        public int MinimumFreeGB;

        [DataMember(Name = "extraLockingProcesses")]
        public List<string> ExtraLockingProcesses;

        [DataMember(Name = "allowForceClose")]
        public bool AllowForceClose;

        [DataMember(Name = "edgeNativeHost")]
        public string EdgeNativeHost;

        [DataMember(Name = "confirmBeforeSync")]
        public bool ConfirmBeforeSync;
    }

    public static class JsonRead
    {
        public static T FromFile<T>(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                return FromStream<T>(fs);
            }
        }

        public static T FromStream<T>(Stream stream)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            return (T)serializer.ReadObject(stream);
        }

        public static T FromText<T>(string text)
        {
            using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            {
                return FromStream<T>(ms);
            }
        }
    }
}
