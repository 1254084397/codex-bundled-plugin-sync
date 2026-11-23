using System;
using System.Collections.Generic;
using System.IO;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// §6.3 component-snapshot backups. M1 implements enumeration (count / newest / status)
    /// which is all the read-only check needs; write paths land with the Sync engine.
    /// </summary>
    public static class BackupStore
    {
        public static BackupsInfo Enumerate(string backupsRoot)
        {
            BackupsInfo info = new BackupsInfo();
            if (!Directory.Exists(backupsRoot))
            {
                return info;
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(backupsRoot);
            }
            catch (Exception)
            {
                return info;
            }

            List<BackupRef> found = new List<BackupRef>();
            List<DateTime> stamps = new List<DateTime>();
            for (int i = 0; i < directories.Length; i++)
            {
                string directory = directories[i];
                string name = Path.GetFileName(directory);
                string manifestPath = Path.Combine(directory, "backup.json");
                BackupRef reference = new BackupRef();
                reference.BackupId = name;
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        BackupManifestDto dto = JsonRead.FromFile<BackupManifestDto>(manifestPath);
                        reference.Status = dto != null && !string.IsNullOrEmpty(dto.Status) ? dto.Status : "unknown";
                        if (dto != null && !string.IsNullOrEmpty(dto.BackupId))
                        {
                            reference.BackupId = dto.BackupId;
                        }
                    }
                    catch (Exception)
                    {
                        reference.Status = "unreadable";
                    }
                    found.Add(reference);
                    try
                    {
                        stamps.Add(Directory.GetLastWriteTimeUtc(directory));
                    }
                    catch (Exception)
                    {
                        stamps.Add(DateTime.MinValue);
                    }
                }
                else if (name.StartsWith(CodexPaths.BackupFolderPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    reference.Status = "legacy";
                    found.Add(reference);
                    try
                    {
                        stamps.Add(Directory.GetLastWriteTimeUtc(directory));
                    }
                    catch (Exception)
                    {
                        stamps.Add(DateTime.MinValue);
                    }
                }
            }

            info.Count = found.Count;
            int newestIndex = -1;
            for (int i = 0; i < stamps.Count; i++)
            {
                if (newestIndex < 0 || stamps[i] > stamps[newestIndex])
                {
                    newestIndex = i;
                }
            }
            if (newestIndex >= 0)
            {
                info.Newest = found[newestIndex];
            }
            return info;
        }
    }
}
