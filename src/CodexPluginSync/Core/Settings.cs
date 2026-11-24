using System;
using System.Collections.Generic;
using System.IO;

namespace CodexPluginSync.Core
{
    /// <summary>§6.2 settings.json. Unreadable or unknown schema falls back to defaults, never blocks startup.</summary>
    public sealed class Settings
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion = CurrentSchemaVersion;
        public string CodexHome;
        public string Language = "auto";
        public string Theme = "auto";
        public bool RunAtStartup = false;
        public int AutoCheckHours = 0;
        public int KeepBackups = 1;
        public int StaleStageHours = 24;
        public int KeepLogDays = 30;
        public int PartialBackupRetentionDays = 7;
        public int FailedBackupRetentionDays = 30;
        public int MinimumFreeGB = 1;
        public List<string> ExtraLockingProcesses = new List<string>();
        public bool AllowForceClose = false;
        public string EdgeNativeHost = "disabled";
        public bool ConfirmBeforeSync = true;

        public static Settings Load(Logger log)
        {
            Settings settings = new Settings();
            string path = CodexPaths.SettingsPath;
            try
            {
                if (!File.Exists(path))
                {
                    return settings;
                }
                SettingsDto dto = JsonRead.FromFile<SettingsDto>(path);
                if (dto == null || dto.SchemaVersion != CurrentSchemaVersion)
                {
                    if (log != null)
                    {
                        log.Warn("settings", "result=fallback-default reason=schema");
                    }
                    return new Settings();
                }
                settings.CopyFrom(dto);
            }
            catch (Exception ex)
            {
                if (log != null)
                {
                    log.Warn("settings", "result=fallback-default error=" + ex.GetType().Name);
                }
                return new Settings();
            }
            return settings;
        }

        private void CopyFrom(SettingsDto dto)
        {
            if (!string.IsNullOrEmpty(dto.CodexHome)) CodexHome = dto.CodexHome;
            if (!string.IsNullOrEmpty(dto.Language)) Language = dto.Language;
            if (!string.IsNullOrEmpty(dto.Theme)) Theme = dto.Theme;
            RunAtStartup = dto.RunAtStartup;
            if (dto.AutoCheckHours >= 0 && dto.AutoCheckHours <= 72)
            {
                AutoCheckHours = dto.AutoCheckHours;
            }
            else
            {
                AutoCheckHours = 0; // 超出 0~72 小时安全范围回退为关闭
            }
            if (dto.KeepBackups >= 0) KeepBackups = dto.KeepBackups;
            if (dto.StaleStageHours > 0) StaleStageHours = dto.StaleStageHours;
            if (dto.KeepLogDays > 0) KeepLogDays = dto.KeepLogDays;
            if (dto.PartialBackupRetentionDays > 0) PartialBackupRetentionDays = dto.PartialBackupRetentionDays;
            if (dto.FailedBackupRetentionDays > 0) FailedBackupRetentionDays = dto.FailedBackupRetentionDays;
            if (dto.MinimumFreeGB > 0) MinimumFreeGB = dto.MinimumFreeGB;
            if (dto.ExtraLockingProcesses != null) ExtraLockingProcesses = dto.ExtraLockingProcesses;
            AllowForceClose = dto.AllowForceClose;
            if (!string.IsNullOrEmpty(dto.EdgeNativeHost)) EdgeNativeHost = dto.EdgeNativeHost;
            ConfirmBeforeSync = dto.ConfirmBeforeSync;
        }

        public string ToJson()
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject();
            w.Prop("schemaVersion", CurrentSchemaVersion);
            w.Prop("codexHome", CodexHome);
            w.Prop("language", Language);
            w.Prop("theme", Theme);
            w.Prop("runAtStartup", RunAtStartup);
            w.Prop("autoCheckHours", AutoCheckHours);
            w.Prop("keepBackups", KeepBackups);
            w.Prop("staleStageHours", StaleStageHours);
            w.Prop("keepLogDays", KeepLogDays);
            w.Prop("partialBackupRetentionDays", PartialBackupRetentionDays);
            w.Prop("failedBackupRetentionDays", FailedBackupRetentionDays);
            w.Prop("minimumFreeGB", MinimumFreeGB);
            w.Name("extraLockingProcesses").BeginArray();
            for (int i = 0; i < ExtraLockingProcesses.Count; i++)
            {
                w.Value(ExtraLockingProcesses[i]);
            }
            w.EndArray();
            w.Prop("allowForceClose", AllowForceClose);
            w.Prop("edgeNativeHost", EdgeNativeHost);
            w.Prop("confirmBeforeSync", ConfirmBeforeSync);
            w.EndObject();
            return w.ToString();
        }

        public void Save()
        {
            string path = CodexPaths.SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, ToJson(), new System.Text.UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
    }
}
