using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace CodexPluginSync.Core
{
    /// <summary>树摘要（§6.3）：文件数 / 总字节 / 排序后的 path|size|sha256 联合哈希。</summary>
    [DataContract]
    public sealed class ComponentDigestDto
    {
        [DataMember(Name = "fileCount")]
        public int FileCount;

        [DataMember(Name = "sizeBytes")]
        public long SizeBytes;

        [DataMember(Name = "treeHash")]
        public string TreeHash;
    }

    /// <summary>§6.3 组件快照。digest = 同步后 live；previousDigest = 备份（同步前）内容。</summary>
    [DataContract]
    public sealed class BackupComponentDto
    {
        [DataMember(Name = "name")]
        public string Name;

        [DataMember(Name = "livePath")]
        public string LivePath;

        [DataMember(Name = "backupPath")]
        public string BackupPath;

        [DataMember(Name = "existedBefore")]
        public bool ExistedBefore;

        [DataMember(Name = "state")]
        public string State;

        [DataMember(Name = "digest")]
        public ComponentDigestDto Digest;

        [DataMember(Name = "previousDigest")]
        public ComponentDigestDto PreviousDigest;
    }

    [DataContract]
    public sealed class BackupNativeHostDto
    {
        [DataMember(Name = "browser")]
        public string Browser;

        [DataMember(Name = "registryPath")]
        public string RegistryPath;

        [DataMember(Name = "registryValueExisted")]
        public bool RegistryValueExisted;

        [DataMember(Name = "previousManifestPath")]
        public string PreviousManifestPath;

        [DataMember(Name = "manifestBackup")]
        public string ManifestBackup;

        [DataMember(Name = "previousTargetPath")]
        public string PreviousTargetPath;

        [DataMember(Name = "applied")]
        public bool Applied;
    }

    public static class BackupStates
    {
        public const string Planned = "planned";
        public const string Moved = "moved";
        public const string Created = "created";
        public const string Partial = "partial";
        public const string Complete = "complete";
        public const string VerifyFailed = "verify-failed";
    }

    /// <summary>
    /// 原子 JSON 写：临时文件 + 落盘 + File.Replace，UTF-8 无 BOM（§6.4）。
    /// 带 BOM 的 JSON 会被 Chrome / ConvertFrom-Json 拒绝，这里统一剥掉。
    /// </summary>
    public static class AtomicJsonFile
    {
        private static readonly byte[] Utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };

        public static byte[] Serialize<T>(T value)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            using (MemoryStream ms = new MemoryStream())
            {
                serializer.WriteObject(ms, value);
                byte[] bytes = ms.ToArray();
                if (bytes.Length >= 3 && bytes[0] == Utf8Bom[0] && bytes[1] == Utf8Bom[1] && bytes[2] == Utf8Bom[2])
                {
                    byte[] trimmed = new byte[bytes.Length - 3];
                    Buffer.BlockCopy(bytes, 3, trimmed, 0, trimmed.Length);
                    return trimmed;
                }
                return bytes;
            }
        }

        public static void Write<T>(string path, T value)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            byte[] bytes = Serialize(value);
            string temp = path + ".tmp";
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            using (FileStream fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, null, true);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        public static bool HasBom(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] head = new byte[3];
                int read = fs.Read(head, 0, 3);
                return read == 3 && head[0] == Utf8Bom[0] && head[1] == Utf8Bom[1] && head[2] == Utf8Bom[2];
            }
        }
    }

    /// <summary>§6.4 journal 写侧：write-ahead + seq 单调递增 + 原子替换。</summary>
    public static class JournalStore
    {
        public static JournalDto Write(CodexPaths paths, JournalDto journal)
        {
            if (journal == null)
            {
                throw new ArgumentNullException("journal");
            }
            if (journal.SchemaVersion == 0)
            {
                journal.SchemaVersion = 1;
            }
            if (journal.Targets == null)
            {
                journal.Targets = new List<JournalTargetDto>();
            }
            journal.Seq = journal.Seq + 1;
            journal.UpdatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
            AtomicJsonFile.Write(paths.JournalPath, journal);
            return journal;
        }

        public static JournalDto Read(CodexPaths paths)
        {
            if (!File.Exists(paths.JournalPath))
            {
                return null;
            }
            return JsonRead.FromFile<JournalDto>(paths.JournalPath);
        }

        public static void Delete(CodexPaths paths)
        {
            if (File.Exists(paths.JournalPath))
            {
                File.Delete(paths.JournalPath);
            }
            string temp = paths.JournalPath + ".tmp";
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>§6.3 backup.json 写侧与完成判据校验。</summary>
    public static class BackupManifestStore
    {
        public const string FileName = "backup.json";

        public static string Write(string backupDirectory, BackupManifestDto manifest)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException("manifest");
            }
            if (manifest.SchemaVersion == 0)
            {
                manifest.SchemaVersion = 1;
            }
            string path = Path.Combine(backupDirectory, FileName);
            AtomicJsonFile.Write(path, manifest);
            return path;
        }

        /// <summary>
        /// 返回 null 表示通过；否则返回可直接写日志/给用户看的原因。
        /// §6.3 硬约束：
        ///   1. components 为空时不允许 status=complete；
        ///   2. status=complete 时每个组件只允许终态 moved / created，且必须有 digest；
        ///   3. moved 必须有 backupPath + previousDigest，且 existedBefore 必须为 true；
        ///   4. created 必须 existedBefore=false。
        /// 未完的 manifest（partial / verify-failed）只做最小检查，允许 state=planned。
        /// </summary>
        public static string Validate(BackupManifestDto manifest)
        {
            if (manifest == null)
            {
                return "backup.json 缺失或不可读";
            }
            if (string.IsNullOrEmpty(manifest.Status))
            {
                return "backup.json 缺少 status";
            }

            bool complete = manifest.Status == BackupStates.Complete;
            if (complete && (manifest.Components == null || manifest.Components.Count == 0))
            {
                return "components 为空时不允许标记 status=complete";
            }
            if (manifest.Components == null)
            {
                return null;
            }

            for (int i = 0; i < manifest.Components.Count; i++)
            {
                BackupComponentDto component = manifest.Components[i];
                string label = (component == null || string.IsNullOrEmpty(component.Name))
                    ? "第 " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " 个组件"
                    : component.Name;
                if (component == null || string.IsNullOrEmpty(component.Name))
                {
                    return label + "缺少 name";
                }

                if (!complete)
                {
                    // 未完成的快照：只要求 moved 已声明备份落点，不要求 digest（尚未校验）。
                    if (component.State == BackupStates.Moved && string.IsNullOrEmpty(component.BackupPath))
                    {
                        return label + "：state=moved 但缺少 backupPath";
                    }
                    continue;
                }

                if (component.State != BackupStates.Moved && component.State != BackupStates.Created)
                {
                    return label + "：status=complete 下不允许 state=" + (component.State == null ? "(空)" : component.State);
                }
                if (component.Digest == null || string.IsNullOrEmpty(component.Digest.TreeHash))
                {
                    return label + "：缺少 digest（无法验证新内容）";
                }

                if (component.State == BackupStates.Moved)
                {
                    if (string.IsNullOrEmpty(component.BackupPath))
                    {
                        return label + "：state=moved 但缺少 backupPath";
                    }
                    if (component.PreviousDigest == null || string.IsNullOrEmpty(component.PreviousDigest.TreeHash))
                    {
                        return label + "：state=moved 但缺少 previousDigest（回滚将无法校验备份内容）";
                    }
                    if (!component.ExistedBefore)
                    {
                        return label + "：state=moved 与 existedBefore=false 矛盾";
                    }
                }
                else if (component.ExistedBefore)
                {
                    return label + "：state=created 与 existedBefore=true 矛盾";
                }
            }
            return null;
        }
    }
}
