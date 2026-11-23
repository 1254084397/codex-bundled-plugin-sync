using System;
using System.Globalization;
using System.IO;
using CodexPluginSync.Core;

namespace CodexPluginSync.Cli
{
    /// <summary>
    /// Human-readable check report. The exact line "Update needed: Yes|No" is part of the
    /// .cmd compatibility surface (§2.4) — do not reword it.
    /// </summary>
    public static class ConsoleReport
    {
        public static void WriteCheck(TextWriter writer, CheckOutcome outcome, bool quiet)
        {
            Snapshot snapshot = outcome.Snapshot;

            if (!quiet)
            {
                if (snapshot.Desktop.Found)
                {
                    string version = string.IsNullOrEmpty(snapshot.Desktop.Version) ? "(fixture)" : snapshot.Desktop.Version;
                    writer.WriteLine("Codex Desktop: " + version);
                }
                else
                {
                    writer.WriteLine("Codex Desktop: not found");
                }
                writer.WriteLine("CodexHome: " + snapshot.CodexHome);

                for (int i = 0; i < snapshot.Disk.Count; i++)
                {
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "Free space on {0}: {1:N1} GB",
                        snapshot.Disk[i].Drive, snapshot.Disk[i].FreeBytes / 1024.0 / 1024.0 / 1024.0));
                }

                if (snapshot.Desktop.Found)
                {
                    writer.WriteLine();
                    writer.WriteLine("Desktop bundled-plugin versions:");
                    WritePlugins(writer, snapshot.Marketplace.DesktopPlugins);
                    writer.WriteLine("Current local marketplace versions:");
                    if (snapshot.Marketplace.LocalPlugins.Count == 0)
                    {
                        writer.WriteLine("  - (not yet synchronized)");
                    }
                    else
                    {
                        WritePlugins(writer, snapshot.Marketplace.LocalPlugins);
                    }
                    writer.WriteLine("Expected persistent marketplace versions:");
                    WritePlugins(writer, snapshot.Marketplace.Materialized);

                    writer.WriteLine();
                    writer.WriteLine("Managed cache versions:");
                    for (int i = 0; i < snapshot.Components.Count; i++)
                    {
                        ComponentInfo component = snapshot.Components[i];
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "  - {0}: {1} (expected {2}, {3})",
                            component.Name,
                            component.Actual ?? "(missing)",
                            component.Expected ?? "(unknown)",
                            component.State));
                    }

                    writer.WriteLine();
                    writer.WriteLine("Chrome native-messaging host: " + snapshot.NativeHost.Chrome.State);
                }

                if (snapshot.LockingProcesses.Count > 0)
                {
                    writer.WriteLine();
                    writer.WriteLine("Warning: processes that may lock plugin files are running:");
                    for (int i = 0; i < snapshot.LockingProcesses.Count; i++)
                    {
                        writer.WriteLine("  - " + snapshot.LockingProcesses[i].Name +
                            " (PID " + snapshot.LockingProcesses[i].Pid.ToString(CultureInfo.InvariantCulture) + ")");
                    }
                }

                if (snapshot.Journal != null)
                {
                    writer.WriteLine();
                    writer.WriteLine("Previous operation did not finish: phase=" + snapshot.Journal.Phase +
                        " operation=" + (snapshot.Journal.OperationId ?? "?"));
                }
            }

            writer.WriteLine();
            writer.WriteLine("Verdict: " + snapshot.Verdict);
            if (string.Equals(snapshot.Verdict, Verdicts.Error, StringComparison.Ordinal))
            {
                if (outcome.ErrorMessage != null)
                {
                    writer.WriteLine("Reason: " + outcome.ErrorMessage);
                }
            }
            else
            {
                writer.WriteLine("Update needed: " + (string.Equals(snapshot.Verdict, Verdicts.UpdateNeeded, StringComparison.Ordinal) ? "Yes" : "No"));
            }
            if (!quiet)
            {
                writer.WriteLine("Rollback backups available: " + snapshot.Backups.Count.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void WritePlugins(TextWriter writer, System.Collections.Generic.List<PluginRef> plugins)
        {
            if (plugins.Count == 0)
            {
                writer.WriteLine("  - (none)");
                return;
            }
            for (int i = 0; i < plugins.Count; i++)
            {
                writer.WriteLine("  - " + plugins[i].Name + "@" + (plugins[i].Version ?? "?"));
            }
        }
    }
}
