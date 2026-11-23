using System;
using System.IO;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// §6.4 read side of pending-operation.json. A present journal means a previous run stopped
    /// mid-transaction; the check reports it as `blocked` and never deletes it.
    /// </summary>
    public static class OperationJournal
    {
        public static JournalInfo TryRead(CodexPaths paths, out bool corrupt)
        {
            corrupt = false;
            string journalPath = paths.JournalPath;
            if (!File.Exists(journalPath))
            {
                return null;
            }

            JournalInfo info = new JournalInfo();
            try
            {
                JournalDto dto = JsonRead.FromFile<JournalDto>(journalPath);
                if (dto == null || string.IsNullOrEmpty(dto.Phase))
                {
                    corrupt = true;
                    info.Phase = "corrupt";
                    return info;
                }
                info.OperationId = dto.OperationId;
                info.Seq = dto.Seq;
                info.Phase = dto.Phase;
                info.UpdatedUtc = dto.UpdatedUtc;
                info.BackupId = dto.BackupId;
                info.TargetCount = dto.Targets != null ? dto.Targets.Count : 0;
                return info;
            }
            catch (Exception)
            {
                corrupt = true;
                info.Phase = "corrupt";
                return info;
            }
        }

        public static bool IsBeforeCommit(string phase)
        {
            if (string.IsNullOrEmpty(phase))
            {
                return true;
            }
            string normalized = phase.Replace("-", string.Empty).ToLowerInvariant();
            return normalized == "preflight" || normalized == "planned" || normalized == "backupcreated" ||
                   normalized == "staged" || normalized == "verified";
        }
    }
}
