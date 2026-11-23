using System.Collections.Generic;

namespace CodexPluginSync.Core
{
    /// <summary>Owns the JSON shape of StateSnapshot (§6.1). Single place where the contract lives.</summary>
    public static class SnapshotWriter
    {
        public static string Write(Snapshot snapshot)
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject();

            w.Prop("timestampUtc", snapshot.TimestampUtc);
            w.Prop("codexHome", snapshot.CodexHome);

            w.Name("desktop").BeginObject();
            w.Prop("found", snapshot.Desktop.Found);
            WriteStringOrNull(w, "version", snapshot.Desktop.Version);
            WriteStringOrNull(w, "packageFullName", snapshot.Desktop.PackageFullName);
            w.EndObject();

            w.Name("marketplace").BeginObject();
            w.Prop("exists", snapshot.Marketplace.Exists);
            w.Prop("needsUpdate", snapshot.Marketplace.NeedsUpdate);
            WritePlugins(w, "desktopPlugins", snapshot.Marketplace.DesktopPlugins);
            WritePlugins(w, "localPlugins", snapshot.Marketplace.LocalPlugins);
            WritePlugins(w, "materialized", snapshot.Marketplace.Materialized);
            w.EndObject();

            w.Name("components").BeginArray();
            for (int i = 0; i < snapshot.Components.Count; i++)
            {
                ComponentInfo component = snapshot.Components[i];
                w.BeginObject();
                w.Prop("type", component.Type);
                w.Prop("name", component.Name);
                WriteStringOrNull(w, "expected", component.Expected);
                WriteStringOrNull(w, "actual", component.Actual);
                w.Prop("state", component.State);
                w.EndObject();
            }
            w.EndArray();

            w.Name("nativeHost").BeginObject();
            w.Name("chrome").BeginObject();
            w.Prop("state", snapshot.NativeHost.Chrome.State);
            w.Prop("registered", snapshot.NativeHost.Chrome.Registered);
            WriteStringOrNull(w, "manifestPath", snapshot.NativeHost.Chrome.ManifestPath);
            WriteStringOrNull(w, "targetPath", snapshot.NativeHost.Chrome.TargetPath);
            w.EndObject();
            w.Name("edge").BeginObject();
            w.Prop("state", snapshot.NativeHost.Edge.State);
            w.Prop("registered", snapshot.NativeHost.Edge.Registered);
            w.EndObject();
            w.EndObject();

            w.Name("efs").BeginObject();
            w.Prop("stagingParentEncrypted", snapshot.Efs.StagingParentEncrypted);
            w.Prop("encryptionSupported", snapshot.Efs.EncryptionSupported);
            w.EndObject();

            w.Name("lockingProcesses").BeginArray();
            for (int i = 0; i < snapshot.LockingProcesses.Count; i++)
            {
                LockingProcess process = snapshot.LockingProcesses[i];
                w.BeginObject();
                w.Prop("name", process.Name);
                w.Prop("pid", process.Pid);
                w.Prop("hasWindow", process.HasWindow);
                w.EndObject();
            }
            w.EndArray();

            w.Name("fileLocks").BeginArray();
            for (int i = 0; i < snapshot.FileLocks.Count; i++)
            {
                w.BeginObject();
                w.Prop("path", snapshot.FileLocks[i].Path);
                w.Prop("component", snapshot.FileLocks[i].Component);
                w.EndObject();
            }
            w.EndArray();

            w.Name("disk").BeginArray();
            for (int i = 0; i < snapshot.Disk.Count; i++)
            {
                w.BeginObject();
                w.Prop("drive", snapshot.Disk[i].Drive);
                w.Prop("freeBytes", snapshot.Disk[i].FreeBytes);
                w.EndObject();
            }
            w.EndArray();

            w.Name("backups").BeginObject();
            w.Prop("count", snapshot.Backups.Count);
            if (snapshot.Backups.Newest == null)
            {
                w.Null("newest");
            }
            else
            {
                w.Name("newest").BeginObject();
                w.Prop("backupId", snapshot.Backups.Newest.BackupId);
                w.Prop("status", snapshot.Backups.Newest.Status);
                w.EndObject();
            }
            w.EndObject();

            if (snapshot.Journal == null)
            {
                w.Null("journal");
            }
            else
            {
                w.Name("journal").BeginObject();
                WriteStringOrNull(w, "operationId", snapshot.Journal.OperationId);
                w.Prop("seq", snapshot.Journal.Seq);
                w.Prop("phase", snapshot.Journal.Phase);
                WriteStringOrNull(w, "updatedUtc", snapshot.Journal.UpdatedUtc);
                WriteStringOrNull(w, "backupId", snapshot.Journal.BackupId);
                w.Prop("targetCount", snapshot.Journal.TargetCount);
                w.EndObject();
            }

            w.Name("environment").BeginObject();
            WriteStringOrNull(w, "osBuild", snapshot.Environment.OsBuild);
            w.Prop("dotnetRelease", snapshot.Environment.DotnetRelease);
            w.Prop("isAdmin", snapshot.Environment.IsAdmin);
            w.EndObject();

            w.Prop("verdict", snapshot.Verdict);
            w.EndObject();
            return w.ToString();
        }

        /// <summary>§13.3 envelope: {schemaVersion, tool, command, exitCode, state}.</summary>
        public static string WriteEnvelope(string toolVersion, string command, int exitCode, string stateJson)
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject();
            w.Prop("schemaVersion", JsonSchemaVersion);
            w.Name("tool").BeginObject();
            w.Prop("name", CodexPaths.ToolName);
            w.Prop("version", toolVersion);
            w.EndObject();
            w.Prop("command", command);
            w.Prop("exitCode", exitCode);
            w.PropRaw("state", stateJson);
            w.EndObject();
            return w.ToString();
        }

        public const int JsonSchemaVersion = 1;

        private static void WritePlugins(JsonWriter w, string name, List<PluginRef> plugins)
        {
            w.Name(name).BeginArray();
            for (int i = 0; i < plugins.Count; i++)
            {
                w.BeginObject();
                w.Prop("name", plugins[i].Name);
                WriteStringOrNull(w, "version", plugins[i].Version);
                w.EndObject();
            }
            w.EndArray();
        }

        private static void WriteStringOrNull(JsonWriter w, string name, string value)
        {
            if (value == null)
            {
                w.Null(name);
            }
            else
            {
                w.Prop(name, value);
            }
        }
    }
}
