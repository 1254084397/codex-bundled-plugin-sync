using System;
using System.Collections.Generic;

namespace CodexPluginSync.Cli
{
    /// <summary>§13.1 argument set. Unknown arguments are a hard error, never silently ignored.</summary>
    public sealed class CliOptions
    {
        public const string CommandHelp = "help";
        public const string CommandVersion = "version";
        public const string CommandCheck = "check";
        public const string CommandSync = "sync";
        public const string CommandRollback = "rollback";
        public const string CommandCleanupOnly = "cleanup-only";
        public const string CommandExportDiag = "export-diag";
        public const string CommandSelfTest = "selftest";

        public string Command = CommandHelp;
        public bool Json;
        public bool Quiet;
        public bool Force;
        public bool DryRun;
        public bool NoRedact;
        public string BackupId;
        public string OutPath;
        public string FixtureDirectory;
        public string SourceBundle;
        public string CodexHomeOverride;

        public static bool TryParse(string[] args, out CliOptions options, out string error)
        {
            options = new CliOptions();
            error = null;
            if (args == null || args.Length == 0)
            {
                options.Command = CommandHelp;
                return true;
            }

            List<string> pendingValues = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                string lower = argument.ToLowerInvariant();
                switch (lower)
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        options.Command = CommandHelp;
                        return true;
                    case "--version":
                        options.Command = CommandVersion;
                        return true;
                    case "--check":
                        options.Command = CommandCheck;
                        break;
                    case "--sync":
                        options.Command = CommandSync;
                        break;
                    case "--rollback":
                        options.Command = CommandRollback;
                        break;
                    case "--cleanup-only":
                        options.Command = CommandCleanupOnly;
                        break;
                    case "--export-diag":
                        options.Command = CommandExportDiag;
                        break;
                    case "--selftest":
                        options.Command = CommandSelfTest;
                        break;
                    case "--json":
                        options.Json = true;
                        break;
                    case "--quiet":
                        options.Quiet = true;
                        break;
                    case "--force":
                        options.Force = true;
                        break;
                    case "--dry-run":
                        options.DryRun = true;
                        break;
                    case "--no-redact":
                        options.NoRedact = true;
                        break;
                    case "--no-pause":
                    case "-no-pause":
                    case "--pause":
                    case "-pause":
                        // 兼容批处理包装器传递的控制台暂停标志，静默忽略
                        break;
                    case "--backup":
                    case "--out":
                    case "--fixture":
                    case "--source-bundle":
                        if (i + 1 >= args.Length)
                        {
                            error = "Missing value for " + argument + ".";
                            return false;
                        }
                        string value = args[++i];
                        if (lower == "--backup")
                        {
                            options.BackupId = value;
                        }
                        else if (lower == "--out")
                        {
                            options.OutPath = value;
                        }
                        else if (lower == "--fixture")
                        {
                            options.FixtureDirectory = value;
                        }
                        else
                        {
                            options.SourceBundle = value;
                        }
                        break;
                    default:
                        error = "Unknown argument: " + argument;
                        return false;
                }
            }
            return true;
        }
    }
}
