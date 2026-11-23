using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CodexPluginSync.Core;
using CodexPluginSync.Tests;

namespace CodexPluginSync.Cli
{
    public static class CliRunner
    {
        public static string ToolVersion
        {
            get
            {
                Version version = typeof(CliRunner).Assembly.GetName().Version;
                return version.ToString(3);
            }
        }

        public static int Run(string[] args)
        {
            CliOptions options;
            string parseError;
            if (!CliOptions.TryParse(args, out options, out parseError))
            {
                Console.Error.WriteLine(parseError);
                Console.Error.WriteLine();
                Console.Error.Write(UsageText());
                return ExitCodes.Error;
            }

            switch (options.Command)
            {
                case CliOptions.CommandHelp:
                    Console.Out.Write(UsageText());
                    return ExitCodes.Ok;
                case CliOptions.CommandVersion:
                    Console.Out.WriteLine(ToolVersion);
                    return ExitCodes.Ok;
                case CliOptions.CommandCheck:
                    return RunCheck(options, Console.Out, Console.Error);
                case CliOptions.CommandSync:
                    return RunSync(options, Console.Out, Console.Error);
                case CliOptions.CommandRollback:
                    return RunRollback(options, Console.Out, Console.Error);
                case CliOptions.CommandCleanupOnly:
                    return RunCleanup(options, Console.Out, Console.Error);
                case CliOptions.CommandExportDiag:
                    return RunExportDiag(options, Console.Out, Console.Error);
                case CliOptions.CommandSelfTest:
                    return SelfTest.Run(options.FixtureDirectory, Console.Out, Console.Error);
                default:
                    Console.Error.WriteLine("Command '" + options.Command +
                        "' is not implemented in this build (M1 increment 1: check only).");
                    Console.Error.WriteLine("Implemented today: --check, --sync, --rollback, --cleanup-only, --export-diag, --selftest, --version, --help.");
                    return ExitCodes.Error;
            }
        }

        public static int RunCheck(CliOptions options, TextWriter stdout, TextWriter stderr)
        {
            Settings settings = Settings.Load(null);
            CheckOptions checkOptions = new CheckOptions();
            checkOptions.CodexHome = CodexPaths.ResolveCodexHome(
                options.CodexHomeOverride ?? settings.CodexHome);
            checkOptions.SourceBundleOverride = options.SourceBundle;
            checkOptions.MinimumFreeGB = settings.MinimumFreeGB;
            checkOptions.ExtraLockingProcesses = settings.ExtraLockingProcesses;
            return ExecuteCheck(checkOptions, options.Json, options.Quiet, stdout, stderr);
        }

        /// <summary>
        /// Shared by the CLI and the self-test so both exercise exactly one code path
        /// (§13.4: the JSON exitCode and the process exit code must come from the same value).
        /// </summary>
        public static int ExecuteCheck(CheckOptions checkOptions, bool json, bool quiet,
            TextWriter stdout, TextWriter stderr)
        {
            Logger log = new Logger(CodexPaths.ToolLogDirectory, "check");
            try
            {
                CheckEngine engine = new CheckEngine(checkOptions, log);
                CheckOutcome outcome = engine.Execute();

                if (json)
                {
                    string state = SnapshotWriter.Write(outcome.Snapshot);
                    stdout.WriteLine(SnapshotWriter.WriteEnvelope(ToolVersion, "check", outcome.ExitCode, state));
                }
                else
                {
                    ConsoleReport.WriteCheck(stdout, outcome, quiet);
                }

                if (outcome.ErrorMessage != null)
                {
                    stderr.WriteLine("Error: " + outcome.ErrorMessage);
                }
                for (int i = 0; i < outcome.Warnings.Count; i++)
                {
                    stderr.WriteLine("Warning: " + outcome.Warnings[i]);
                }
                if (!quiet && log.FilePath != null)
                {
                    stderr.WriteLine("Log: " + log.FilePath);
                }
                return outcome.ExitCode;
            }
            finally
            {
                log.Dispose();
            }
        }

        public static int RunSync(CliOptions options, TextWriter stdout, TextWriter stderr)
        {
            Settings settings = Settings.Load(null);
            SyncOptions syncOptions = new SyncOptions();
            syncOptions.CodexHome = options.CodexHomeOverride ?? settings.CodexHome;
            syncOptions.SourceBundleOverride = options.SourceBundle;
            syncOptions.Force = options.Force;
            syncOptions.Out = stdout;
            syncOptions.Err = stderr;

            SyncRunResult result = SyncEngine.Run(syncOptions);
            if (result.ExitCode != ExitCodes.Ok && !string.IsNullOrEmpty(result.Message))
            {
                stderr.WriteLine(result.Message);
            }
            return result.ExitCode;
        }

        public static int RunRollback(CliOptions options, TextWriter stdout, TextWriter stderr)
        {
            Settings settings = Settings.Load(null);
            RollbackOptions rollbackOptions = new RollbackOptions();
            rollbackOptions.CodexHome = options.CodexHomeOverride ?? settings.CodexHome;
            rollbackOptions.BackupId = options.BackupId;
            rollbackOptions.Out = stdout;
            rollbackOptions.Err = stderr;

            SyncRunResult result = RollbackEngine.Run(rollbackOptions);
            return result.ExitCode;
        }

        public static int RunCleanup(CliOptions options, TextWriter stdout, TextWriter stderr)
        {
            Settings settings = Settings.Load(null);
            CleanupOptions cleanupOptions = new CleanupOptions();
            cleanupOptions.CodexHome = options.CodexHomeOverride ?? settings.CodexHome;
            cleanupOptions.DryRun = options.DryRun;
            cleanupOptions.Out = stdout;
            cleanupOptions.Err = stderr;

            SyncRunResult result = CleanupEngine.Run(cleanupOptions);
            return result.ExitCode;
        }

        public static int RunExportDiag(CliOptions options, TextWriter stdout, TextWriter stderr)
        {
            Settings settings = Settings.Load(null);
            DiagOptions diagOptions = new DiagOptions();
            diagOptions.CodexHome = options.CodexHomeOverride ?? settings.CodexHome;
            diagOptions.OutPath = options.OutPath;
            diagOptions.NoRedact = options.NoRedact;
            diagOptions.Out = stdout;
            diagOptions.Err = stderr;

            SyncRunResult result = DiagExporter.Run(diagOptions);
            return result.ExitCode;
        }
        public static string UsageText()
        {
            StringWriter writer = new StringWriter();
            writer.WriteLine("CodexPluginSync " + ToolVersion);
            writer.WriteLine("Keeps Codex Desktop bundled plugins in sync with the persistent marketplace and caches.");
            writer.WriteLine();
            writer.WriteLine("Usage:");
            writer.WriteLine("  CodexPluginSync.exe                          GUI (M3; prints this help until then)");
            writer.WriteLine("  CodexPluginSync.exe --check [--json] [--quiet]     read-only check");
            writer.WriteLine("  CodexPluginSync.exe --sync [--force] [--source-bundle <dir>]  run the sync transaction");
            writer.WriteLine("  CodexPluginSync.exe --rollback [--backup <id>]     restore a backup (newest status=complete by default)");
            writer.WriteLine("  CodexPluginSync.exe --cleanup-only [--dry-run]     remove stale stages, old backups and old logs");
            writer.WriteLine("  CodexPluginSync.exe --export-diag [--out <path>] [--no-redact]   write a redacted diagnostic zip");
            writer.WriteLine("  CodexPluginSync.exe --selftest [--fixture <dir>]   fixture end-to-end self-test");
            writer.WriteLine("  CodexPluginSync.exe --version | --help");
            writer.WriteLine();
            writer.WriteLine("Exit codes: 0 ok/no update needed, 1 error, 2 --check found an update.");
            writer.WriteLine("CodexHome resolution: CODEX_HOME environment variable, else %USERPROFILE%\\CodexHome.");
            return writer.ToString();
        }
    }
}
