using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using CodexPluginSync.Cli;
using CodexPluginSync.Core;

namespace CodexPluginSync.Tests
{
    /// <summary>
    /// §16.1 fixture suite. Shipped inside the exe (`--selftest`) so CI and users can prove a build
    /// works without a Codex install. Cases currently cover the read-only check contract; the
    /// transaction cases are added with the Sync engine.
    /// </summary>
    public static class SelfTest
    {
        [DataContract]
        private sealed class EnvelopeDto
        {
            [DataMember(Name = "schemaVersion")]
            public int SchemaVersion;

            [DataMember(Name = "command")]
            public string Command;

            [DataMember(Name = "exitCode")]
            public int ExitCode;

            [DataMember(Name = "tool")]
            public ToolDto Tool;

            [DataMember(Name = "state")]
            public StateDto State;
        }

        [DataContract]
        private sealed class ToolDto
        {
            [DataMember(Name = "name")]
            public string Name;

            [DataMember(Name = "version")]
            public string Version;
        }

        [DataContract]
        private sealed class StateDto
        {
            [DataMember(Name = "verdict")]
            public string Verdict;

            [DataMember(Name = "codexHome")]
            public string CodexHome;

            [DataMember(Name = "marketplace")]
            public MarketplaceDto Marketplace;

            [DataMember(Name = "components")]
            public List<ComponentDto> Components;

            [DataMember(Name = "journal")]
            public JournalStateDto Journal;

            [DataMember(Name = "backups")]
            public BackupsDto Backups;

            [DataMember(Name = "efs")]
            public EfsDto Efs;
        }

        [DataContract]
        private sealed class MarketplaceDto
        {
            [DataMember(Name = "exists")]
            public bool Exists;

            [DataMember(Name = "needsUpdate")]
            public bool? NeedsUpdate;
        }

        [DataContract]
        private sealed class ComponentDto
        {
            [DataMember(Name = "name")]
            public string Name;

            [DataMember(Name = "expected")]
            public string Expected;

            [DataMember(Name = "actual")]
            public string Actual;

            [DataMember(Name = "state")]
            public string State;
        }

        [DataContract]
        private sealed class JournalStateDto
        {
            [DataMember(Name = "phase")]
            public string Phase;
        }

        [DataContract]
        private sealed class EfsDto
        {
            [DataMember(Name = "encryptionSupported")]
            public bool? EncryptionSupported;

            [DataMember(Name = "stagingParentEncrypted")]
            public bool? StagingParentEncrypted;
        }

        [DataContract]
        private sealed class BackupsDto
        {
            [DataMember(Name = "count")]
            public int Count;
        }

        private sealed class TestContext
        {
            public List<string> Failures = new List<string>();
            public int CaseCount;
            public TextWriter Out;

            public void Begin(string name)
            {
                CaseCount++;
                Out.WriteLine("[case] " + name);
            }

            public void Expect(bool condition, string what, string actual)
            {
                if (condition)
                {
                    Out.WriteLine("   ok   " + what);
                }
                else
                {
                    Failures.Add(what + " (actual: " + actual + ")");
                    Out.WriteLine("   FAIL " + what + "  actual=" + actual);
                }
            }

            public void ExpectEqual(string expected, string actual, string what)
            {
                Expect(string.Equals(expected, actual, StringComparison.Ordinal),
                    what + " == " + expected, actual ?? "(null)");
            }

            public void ExpectEqual(int expected, int actual, string what)
            {
                Expect(expected == actual, what + " == " + expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    actual.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        public static int Run(string fixtureRoot, TextWriter stdout, TextWriter stderr)
        {
            bool keep = !string.IsNullOrEmpty(fixtureRoot);
            string baseDirectory = keep
                ? fixtureRoot
                : Path.Combine(Path.GetTempPath(), "CodexPluginSync-selftest-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(baseDirectory);
            TestContext context = new TestContext();
            context.Out = stdout;
            stdout.WriteLine("CodexPluginSync self-test");
            stdout.WriteLine("fixture root: " + baseDirectory);
            stdout.WriteLine();

            try
            {
                NoUpdateCase(context, baseDirectory);
                OutdatedCacheCase(context, baseDirectory);
                MarketplaceMissingCase(context, baseDirectory);
                CacheMissingCase(context, baseDirectory);
                JournalBlockedCase(context, baseDirectory);
                SourceBundleMissingCase(context, baseDirectory);
                CodexHomeMissingCase(context, baseDirectory);
                JsonContractCase(context, baseDirectory);
                ReadOnlyCase(context, baseDirectory);
                BackupAndEnvironmentCase(context, baseDirectory);
                JunctionEquivalenceCase(context, baseDirectory);
                ArgumentParsingCase(context);
            }
            finally
            {
                if (!keep)
                {
                    try
                    {
                        Directory.Delete(baseDirectory, true);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            stdout.WriteLine();
            if (context.Failures.Count == 0)
            {
                stdout.WriteLine("SELFTEST PASS (" + context.CaseCount.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) + " cases)");
                return ExitCodes.Ok;
            }
            stdout.WriteLine("SELFTEST FAIL (" + context.Failures.Count.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + " failed of " +
                context.CaseCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " cases)");
            for (int i = 0; i < context.Failures.Count; i++)
            {
                stdout.WriteLine("  - " + context.Failures[i]);
            }
            if (stderr != null)
            {
                stderr.WriteLine("Self-test failed.");
            }
            return ExitCodes.Error;
        }

        // ---------------------------------------------------------------- cases

        private static void NoUpdateCase(TestContext context, string baseDirectory)
        {
            context.Begin("check/no-update");
            using (Fixture fixture = new Fixture(baseDirectory, "no-update"))
            {
                fixture.AddDesktopPlugin("browser", "1.4.0");
                fixture.AddLocalPlugin("browser", "1.4.0");
                fixture.SetMaterializationKey("browser");
                fixture.AddCache("browser", "1.4.0");

                int exitCode;
                string stdoutText;
                CheckOptions options = BuildOptions(fixture);

                StringWriter outWriter = new StringWriter();
                StringWriter errWriter = new StringWriter();
                exitCode = CliRunner.ExecuteCheck(options, false, false, outWriter, errWriter);
                stdoutText = outWriter.ToString();

                context.ExpectEqual(ExitCodes.Ok, exitCode, "exit code");
                context.Expect(stdoutText.Contains("Update needed: No"), "human output says 'Update needed: No'", FirstLine(stdoutText, "Update needed"));
                context.Expect(stdoutText.Contains("Verdict: ok"), "human output says 'Verdict: ok'", FirstLine(stdoutText, "Verdict"));
            }
        }

        private static void OutdatedCacheCase(TestContext context, string baseDirectory)
        {
            context.Begin("check/update-needed-outdated-cache");
            using (Fixture fixture = new Fixture(baseDirectory, "outdated-cache"))
            {
                fixture.AddDesktopPlugin("browser", "1.4.0");
                fixture.AddLocalPlugin("browser", "1.4.0");
                fixture.SetMaterializationKey("browser");
                fixture.AddCache("browser", "1.3.2");

                StringWriter outWriter = new StringWriter();
                int exitCode = CliRunner.ExecuteCheck(BuildOptions(fixture), false, false, outWriter, new StringWriter());
                context.ExpectEqual(ExitCodes.NeedsUpdate, exitCode, "exit code");
                context.Expect(outWriter.ToString().Contains("Update needed: Yes"), "human output says 'Update needed: Yes'",
                    FirstLine(outWriter.ToString(), "Update needed"));

                EnvelopeDto envelope;
                int jsonExit;
                string jsonStdout;
                RunJson(fixture, out jsonExit, out jsonStdout, out envelope);
                context.ExpectEqual(ExitCodes.NeedsUpdate, jsonExit, "json exit code");
                context.ExpectEqual("update-needed", envelope.State.Verdict, "state.verdict");
                ComponentDto component = FindComponent(envelope, "browser");
                context.Expect(component != null, "component browser present", "null");
                if (component != null)
                {
                    context.ExpectEqual("outdated", component.State, "browser cache state");
                    context.ExpectEqual("1.4.0", component.Expected, "browser expected version");
                    context.ExpectEqual("1.3.2", component.Actual, "browser actual version");
                }
            }
        }

        private static void MarketplaceMissingCase(TestContext context, string baseDirectory)
        {
            context.Begin("check/marketplace-missing");
            using (Fixture fixture = new Fixture(baseDirectory, "marketplace-missing"))
            {
                fixture.AddDesktopPlugin("browser", "1.4.0");
                fixture.AddCache("browser", "1.4.0");
                fixture.SetBundleMarketplaceManifest("browser");

                EnvelopeDto envelope;
                int exitCode;
                string stdoutText;
                RunJson(fixture, out exitCode, out stdoutText, out envelope);
                context.ExpectEqual(ExitCodes.NeedsUpdate, exitCode, "exit code");
                context.ExpectEqual("update-needed", envelope.State.Verdict, "state.verdict");
                context.Expect(!envelope.State.Marketplace.Exists, "marketplace.exists is false",
                    envelope.State.Marketplace.Exists.ToString());
                context.Expect(envelope.State.Marketplace.NeedsUpdate == true, "marketplace.needsUpdate is true",
                    envelope.State.Marketplace.NeedsUpdate.ToString());
            }
        }

        private static void CacheMissingCase(TestContext context, string baseDirectory)
        {
            context.Begin("check/cache-missing");
            using (Fixture fixture = new Fixture(baseDirectory, "cache-missing"))
            {
                fixture.AddDesktopPlugin("chrome", "1.4.0");
                fixture.AddLocalPlugin("chrome", "1.4.0");
                fixture.SetMaterializationKey("chrome");

                EnvelopeDto envelope;
                int exitCode;
                string stdoutText;
                RunJson(fixture, out exitCode, out stdoutText, out envelope);
                context.ExpectEqual(ExitCodes.NeedsUpdate, exitCode, "exit code");
                ComponentDto component = FindComponent(envelope, "chrome");
                context.Expect(component != null, "component chrome present", "null");
                if (component != null)
                {
                    context.ExpectEqual("missing", component.State, "chrome cache state");
                }
            }
        }

        private static void JournalBlockedCase(TestContext context, string baseDirectory)
        {
            context.Begin("check/pending-journal-blocked");
            using (Fixture fixture = new Fixture(baseDirectory, "journal-blocked"))
            {
                fixture.AddDesktopPlugin("browser", "1.4.0");
                fixture.AddLocalPlugin("browser", "1.4.0");
                fixture.SetMaterializationKey("browser");
                fixture.AddCache("browser", "1.4.0");
                fixture.WriteJournal("commit-started");

                EnvelopeDto envelope;
                int exitCode;
                string stdoutText;
                RunJson(fixture, out exitCode, out stdoutText, out envelope);
                context.ExpectEqual(ExitCodes.Error, exitCode, "exit code");
                context.ExpectEqual("blocked", envelope.State.Verdict, "state.verdict");
                context.Expect(envelope.State.Journal != null, "journal object present", "null");
                if (envelope.State.Journal != null)
                {
                    context.ExpectEqual("commit-started", envelope.State.Journal.Phase, "journal.phase");
                }
                context.Expect(File.Exists(Path.Combine(fixture.CodexHome, "pending-operation.json")),
                    "journal file is never deleted by check", "missing");
            }
        }

        private static void SourceBundleMissingCase(TestContext context, string baseDirectory)
        {
            context.Begin("check/source-bundle-missing");
            using (Fixture fixture = new Fixture(baseDirectory, "source-missing"))
            {
                fixture.SourceBundle = Path.Combine(fixture.Root, "does-not-exist");

                EnvelopeDto envelope;
                int exitCode;
                string stdoutText;
                RunJson(fixture, out exitCode, out stdoutText, out envelope);
                context.ExpectEqual(ExitCodes.Error, exitCode, "exit code");
                context.ExpectEqual("error", envelope.State.Verdict, "state.verdict");
            }
        }

        private static void CodexHomeMissingCase(TestContext context, string baseDirectory)
        {
            context.Begin("check/codex-home-missing");
            using (Fixture fixture = new Fixture(baseDirectory, "home-missing"))
            {
                fixture.AddDesktopPlugin("browser", "1.4.0");
                string realHome = fixture.CodexHome;
                fixture.CodexHome = Path.Combine(fixture.Root, "no-such-codexhome");

                EnvelopeDto envelope;
                int exitCode;
                string stdoutText;
                RunJson(fixture, out exitCode, out stdoutText, out envelope);
                context.ExpectEqual(ExitCodes.Error, exitCode, "exit code");
                context.ExpectEqual("error", envelope.State.Verdict, "state.verdict");
                context.Expect(!Directory.Exists(fixture.CodexHome), "check did not create CodexHome", "created");
                fixture.CodexHome = realHome;
            }
        }

        private static void JsonContractCase(TestContext context, string baseDirectory)
        {
            context.Begin("check/json-contract");
            using (Fixture fixture = new Fixture(baseDirectory, "json-contract"))
            {
                fixture.AddDesktopPlugin("browser", "1.4.0");
                fixture.AddLocalPlugin("browser", "1.4.0");
                fixture.SetMaterializationKey("browser");
                fixture.AddCache("browser", "1.4.0");

                EnvelopeDto envelope;
                int exitCode;
                string stdoutText;
                RunJson(fixture, out exitCode, out stdoutText, out envelope);

                string trimmed = stdoutText.Trim();
                context.Expect(trimmed.StartsWith("{", StringComparison.Ordinal) &&
                    trimmed.EndsWith("}", StringComparison.Ordinal), "stdout is a single JSON object",
                    trimmed.Length > 40 ? trimmed.Substring(0, 40) : trimmed);
                context.Expect(!stdoutText.Contains("Update needed"), "stdout contains no human text", "found text");
                context.ExpectEqual(1, envelope.SchemaVersion, "schemaVersion");
                context.ExpectEqual("check", envelope.Command, "command");
                context.Expect(envelope.Tool != null, "tool object present", "null");
                if (envelope.Tool != null)
                {
                    context.ExpectEqual("CodexPluginSync", envelope.Tool.Name, "tool.name");
                    context.ExpectEqual(CliRunner.ToolVersion, envelope.Tool.Version, "tool.version");
                }
                context.ExpectEqual(exitCode, envelope.ExitCode, "envelope.exitCode matches process exit code");
                context.ExpectEqual(fixture.CodexHome, envelope.State.CodexHome, "state.codexHome");
                context.ExpectEqual("ok", envelope.State.Verdict, "state.verdict");
            }
        }

        private static void ReadOnlyCase(TestContext context, string baseDirectory)
        {
            context.Begin("check/read-only");
            using (Fixture fixture = new Fixture(baseDirectory, "read-only"))
            {
                fixture.AddDesktopPlugin("browser", "1.4.0");
                fixture.AddLocalPlugin("browser", "1.4.0");
                fixture.SetMaterializationKey("browser");
                fixture.AddCache("browser", "1.4.0");

                string before = Fixture.Fingerprint(fixture.CodexHome);
                CliRunner.ExecuteCheck(BuildOptions(fixture), false, true, new StringWriter(), new StringWriter());
                string after = Fixture.Fingerprint(fixture.CodexHome);
                context.ExpectEqual(before, after, "CodexHome tree unchanged by --check");
                context.Expect(!File.Exists(Path.Combine(fixture.CodexHome, "pending-operation.json")),
                    "no journal written by --check", "journal exists");

                EnvelopeDto envelope;
                int exitCode;
                string stdoutText;
                RunJson(fixture, out exitCode, out stdoutText, out envelope);
                context.Expect(envelope.State.Efs != null, "efs object present", "null");
                if (envelope.State.Efs != null)
                {
                    context.Expect(!envelope.State.Efs.EncryptionSupported.HasValue,
                        "efs.encryptionSupported is null (--check does not probe)", envelope.State.Efs.EncryptionSupported.ToString());
                }
            }
        }

        private static void BackupAndEnvironmentCase(TestContext context, string baseDirectory)
        {
            context.Begin("check/backups-and-environment");
            using (Fixture fixture = new Fixture(baseDirectory, "backups"))
            {
                fixture.AddDesktopPlugin("browser", "1.4.0");
                fixture.AddLocalPlugin("browser", "1.4.0");
                fixture.SetMaterializationKey("browser");
                fixture.AddCache("browser", "1.4.0");
                fixture.WriteBackup("20260612-143200", "complete");

                EnvelopeDto envelope;
                int exitCode;
                string stdoutText;
                RunJson(fixture, out exitCode, out stdoutText, out envelope);
                context.ExpectEqual(ExitCodes.Ok, exitCode, "exit code");
                context.Expect(envelope.State.Backups != null, "backups object present", "null");
                if (envelope.State.Backups != null)
                {
                    context.ExpectEqual(1, envelope.State.Backups.Count, "backups.count");
                }
            }
        }

        // Real-machine regression: cache\<plugin>\latest is a junction to the version directory,
        // and Chrome's native-messaging manifest legitimately points at the junction path.
        private static void JunctionEquivalenceCase(TestContext context, string baseDirectory)
        {
            context.Begin("nativehost/junction-equivalence");
            using (Fixture fixture = new Fixture(baseDirectory, "junction"))
            {
                string versionDirectory = Path.Combine(Path.Combine(fixture.CacheParent, "chrome"), "1.4.0");
                Directory.CreateDirectory(Path.Combine(Path.Combine(versionDirectory, "extension-host"), @"windowsd"));
                string expectedExe = Path.Combine(Path.Combine(Path.Combine(versionDirectory, "extension-host"), @"windowsd"), "extension-host.exe");
                File.WriteAllText(expectedExe, "stub");

                string latest = Path.Combine(Path.Combine(fixture.CacheParent, "chrome"), "latest");
                bool created = Fixture.TryCreateJunction(latest, versionDirectory);
                context.Expect(created, "junction created (mklink /J)", created.ToString());
                if (!created)
                {
                    return;
                }

                string viaJunction = Path.Combine(Path.Combine(Path.Combine(latest, "extension-host"), @"windowsd"), "extension-host.exe");
                context.Expect(File.Exists(viaJunction), "executable visible through the junction", "missing");
                context.Expect(PathIdentity.AreSameTarget(viaJunction, expectedExe),
                    "junction path and version path are the same target", "different");

                string otherVersion = Path.Combine(Path.Combine(fixture.CacheParent, "chrome"), "1.3.2");
                Directory.CreateDirectory(Path.Combine(Path.Combine(otherVersion, "extension-host"), @"windowsd"));
                string otherExe = Path.Combine(Path.Combine(Path.Combine(otherVersion, "extension-host"), @"windowsd"), "extension-host.exe");
                File.WriteAllText(otherExe, "stub");
                context.Expect(!PathIdentity.AreSameTarget(otherExe, expectedExe),
                    "a different version is not treated as the same target", "same");
            }
        }

        private static void ArgumentParsingCase(TestContext context)
        {
            context.Begin("cli/argument-parsing");
            CliOptions parsed;
            string error;
            bool ok = CliOptions.TryParse(new string[] { "--check", "--json" }, out parsed, out error);
            context.Expect(ok, "--check --json parses", error ?? "ok");
            if (ok)
            {
                context.ExpectEqual(CliOptions.CommandCheck, parsed.Command, "command");
                context.Expect(parsed.Json, "--json flag set", parsed.Json.ToString());
            }

            CliOptions invalid;
            string invalidError;
            bool rejected = !CliOptions.TryParse(new string[] { "--bogus" }, out invalid, out invalidError);
            context.Expect(rejected, "unknown argument is rejected", "accepted");

            CliOptions missingValue;
            string missingValueError;
            bool rejectedValue = !CliOptions.TryParse(new string[] { "--backup" }, out missingValue, out missingValueError);
            context.Expect(rejectedValue, "missing option value is rejected", "accepted");
        }

        // -------------------------------------------------------------- helpers

        private static CheckOptions BuildOptions(Fixture fixture)
        {
            CheckOptions options = new CheckOptions();
            options.CodexHome = fixture.CodexHome;
            options.SourceBundleOverride = fixture.SourceBundle;
            options.MinimumFreeGB = 1;
            return options;
        }

        private static void RunJson(Fixture fixture, out int exitCode, out string stdoutText, out EnvelopeDto envelope)
        {
            StringWriter outWriter = new StringWriter();
            StringWriter errWriter = new StringWriter();
            exitCode = CliRunner.ExecuteCheck(BuildOptions(fixture), true, true, outWriter, errWriter);
            stdoutText = outWriter.ToString();
            envelope = JsonRead.FromText<EnvelopeDto>(stdoutText.Trim());
        }

        private static ComponentDto FindComponent(EnvelopeDto envelope, string name)
        {
            if (envelope.State.Components == null)
            {
                return null;
            }
            for (int i = 0; i < envelope.State.Components.Count; i++)
            {
                if (string.Equals(envelope.State.Components[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return envelope.State.Components[i];
                }
            }
            return null;
        }

        private static string FirstLine(string text, string token)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return lines[i].Trim();
                }
            }
            return "(not found)";
        }
    }
}
