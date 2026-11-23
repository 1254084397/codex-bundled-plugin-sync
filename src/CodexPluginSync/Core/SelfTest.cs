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
                RecoveryMatrixCase(context);
                TransactionStateCase(context);
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

        /// <summary>
        /// §6.6 矩阵：逐行断言（与脚本层 tests/run-tests.ps1 的 recovery-row* 用例同口径）。
        /// 这是纯逻辑，不需要 fixture 目录。
        /// </summary>
        private static void RecoveryMatrixCase(TestContext context)
        {
            context.Begin("recovery/matrix");

            RecoveryMatrix.TargetState s = new RecoveryMatrix.TargetState();
            s.Component = "cache:browser";

            // 行 1：live=旧、无 backup、无 stage
            s.LiveExists = true;
            s.LiveIsOld = true;
            RecoveryMatrix.Decision d = RecoveryMatrix.ResolveSync(s);
            ExpectRow(context, "row1", d, 1, RecoveryMatrix.OutcomeAborted, RecoveryMatrix.ActionNone);

            // 行 2：已 staged 未 commit
            s.StageExists = true;
            s.StageIsNew = true;
            d = RecoveryMatrix.ResolveSync(s);
            ExpectRow(context, "row2", d, 2, RecoveryMatrix.OutcomeAborted, RecoveryMatrix.ActionDeleteStage);

            // 行 3：backup 已生成、live 未动
            s.StageExists = false;
            s.StageIsNew = false;
            s.BackupExists = true;
            s.BackupIsOld = true;
            d = RecoveryMatrix.ResolveSync(s);
            ExpectRow(context, "row3", d, 3, RecoveryMatrix.OutcomeRecovered, RecoveryMatrix.ActionNone);

            // 行 10：三份齐全且未交换
            s.StageExists = true;
            s.StageIsNew = true;
            d = RecoveryMatrix.ResolveSync(s);
            ExpectRow(context, "row10", d, 10, RecoveryMatrix.OutcomeAborted, RecoveryMatrix.ActionDeleteStage);

            // stage 存在但不完整：必须先落行 11，不得当成 row6/row10 清掉
            s.StageIsNew = false;
            d = RecoveryMatrix.ResolveSync(s);
            ExpectRow(context, "row11-corrupt-stage", d, 11, RecoveryMatrix.OutcomeAttention, RecoveryMatrix.ActionAttention);
            s.StageExists = false;
            s.StageIsNew = false;

            // 行 4：live 缺失、backup 旧、stage 完整 → 继续前滚
            RecoveryMatrix.TargetState m = new RecoveryMatrix.TargetState();
            m.Component = "marketplace";
            m.BackupExists = true;
            m.BackupIsOld = true;
            m.StageExists = true;
            m.StageIsNew = true;
            d = RecoveryMatrix.ResolveSync(m);
            ExpectRow(context, "row4", d, 4, RecoveryMatrix.OutcomeCommitted, RecoveryMatrix.ActionCompleteSwap);

            // 行 8：live 与 stage 都缺失，只能回填备份
            m.StageExists = false;
            m.StageIsNew = false;
            d = RecoveryMatrix.ResolveSync(m);
            ExpectRow(context, "row8", d, 8, RecoveryMatrix.OutcomeRecovered, RecoveryMatrix.ActionRestoreBackup);

            // 行 5 / 6：交换完成
            RecoveryMatrix.TargetState c = new RecoveryMatrix.TargetState();
            c.Component = "cache:chrome";
            c.LiveExists = true;
            c.LiveIsNew = true;
            c.BackupExists = true;
            c.BackupIsOld = true;
            d = RecoveryMatrix.ResolveSync(c);
            ExpectRow(context, "row5", d, 5, RecoveryMatrix.OutcomeCommitted, RecoveryMatrix.ActionNone);
            c.StageExists = true;
            c.StageIsNew = true;
            d = RecoveryMatrix.ResolveSync(c);
            ExpectRow(context, "row6", d, 6, RecoveryMatrix.OutcomeCommitted, RecoveryMatrix.ActionDeleteStage);

            // 行 7：本次新建的组件
            RecoveryMatrix.TargetState n = new RecoveryMatrix.TargetState();
            n.Component = "cache:computer-use";
            n.ExistedBefore = false;
            n.LiveExists = true;
            n.LiveIsNew = true;
            d = RecoveryMatrix.ResolveSync(n);
            ExpectRow(context, "row7", d, 7, RecoveryMatrix.OutcomeCommitted, RecoveryMatrix.ActionNone);

            // 行 9：三份都不存在
            RecoveryMatrix.TargetState z = new RecoveryMatrix.TargetState();
            z.Component = "cache:computer-use";
            d = RecoveryMatrix.ResolveSync(z);
            ExpectRow(context, "row9", d, 9, RecoveryMatrix.OutcomeAttention, RecoveryMatrix.ActionAttention);

            // 行 11：摘要无法识别
            RecoveryMatrix.TargetState u = new RecoveryMatrix.TargetState();
            u.Component = "cache:browser";
            u.LiveExists = true;
            u.BackupExists = true;
            u.BackupIsOld = true;
            d = RecoveryMatrix.ResolveSync(u);
            ExpectRow(context, "row11", d, 11, RecoveryMatrix.OutcomeAttention, RecoveryMatrix.ActionAttention);

            // backup 存在但内容不可识别时不得回填
            RecoveryMatrix.TargetState bad = new RecoveryMatrix.TargetState();
            bad.Component = "cache:browser";
            bad.BackupExists = true;
            d = RecoveryMatrix.ResolveSync(bad);
            ExpectRow(context, "row11-corrupt-backup", d, 11, RecoveryMatrix.OutcomeAttention, RecoveryMatrix.ActionAttention);

            // 回滚：已生效 → 退回安全快照
            RecoveryMatrix.TargetState r = new RecoveryMatrix.TargetState();
            r.Component = "marketplace";
            r.LiveExists = true;
            r.LiveIsNew = true;
            r.SafetyExists = true;
            r.SafetyIsOld = true;
            d = RecoveryMatrix.ResolveRollback(r);
            ExpectRow(context, "rollback-5", d, 5, RecoveryMatrix.OutcomeRecovered, RecoveryMatrix.ActionRestoreSafety);

            // 回滚：live 未动 → 未提交终态
            r.LiveIsNew = false;
            r.LiveIsOld = true;
            r.SafetyExists = false;
            r.SafetyIsOld = false;
            d = RecoveryMatrix.ResolveRollback(r);
            ExpectRow(context, "rollback-2", d, 2, RecoveryMatrix.OutcomeAborted, RecoveryMatrix.ActionNone);

            // 撤回：正常目标统一 reverted，不参与混合终态误判
            RecoveryMatrix.TargetState v = new RecoveryMatrix.TargetState();
            v.Component = "cache:browser";
            v.LiveExists = true;
            v.LiveIsOld = true;
            d = RecoveryMatrix.ResolveRevert(v, false);
            ExpectRow(context, "revert-old", d, 1, RecoveryMatrix.OutcomeReverted, RecoveryMatrix.ActionNone);

            // 撤回：备份内容不可识别时不得回填
            RecoveryMatrix.TargetState v2 = new RecoveryMatrix.TargetState();
            v2.Component = "cache:browser";
            v2.LiveExists = true;
            v2.LiveIsNew = true;
            v2.BackupExists = true;
            d = RecoveryMatrix.ResolveRevert(v2, false);
            ExpectRow(context, "revert-corrupt-backup", d, 11, RecoveryMatrix.OutcomeAttention, RecoveryMatrix.ActionAttention);

            // 终态一致性：committed + aborted 混合必须报不一致
            List<RecoveryMatrix.Decision> mixed = new List<RecoveryMatrix.Decision>();
            mixed.Add(new RecoveryMatrix.Decision(5, RecoveryMatrix.ActionNone, RecoveryMatrix.OutcomeCommitted, "ok"));
            mixed.Add(new RecoveryMatrix.Decision(1, RecoveryMatrix.ActionNone, RecoveryMatrix.OutcomeAborted, "ok"));
            context.Expect(RecoveryMatrix.FindInconsistentOutcome(mixed) != null, "一致性：committed+aborted 必须判为不一致", RecoveryMatrix.Describe(mixed));

            List<RecoveryMatrix.Decision> recoveredAndAborted = new List<RecoveryMatrix.Decision>();
            recoveredAndAborted.Add(new RecoveryMatrix.Decision(5, RecoveryMatrix.ActionRestoreSafety, RecoveryMatrix.OutcomeRecovered, "ok"));
            recoveredAndAborted.Add(new RecoveryMatrix.Decision(2, RecoveryMatrix.ActionNone, RecoveryMatrix.OutcomeAborted, "ok"));
            context.Expect(RecoveryMatrix.FindInconsistentOutcome(recoveredAndAborted) != null, "一致性：recovered+aborted 必须判为不一致", RecoveryMatrix.Describe(recoveredAndAborted));

            List<RecoveryMatrix.Decision> same = new List<RecoveryMatrix.Decision>();
            same.Add(new RecoveryMatrix.Decision(5, RecoveryMatrix.ActionNone, RecoveryMatrix.OutcomeReverted, "ok"));
            same.Add(new RecoveryMatrix.Decision(1, RecoveryMatrix.ActionNone, RecoveryMatrix.OutcomeReverted, "ok"));
            context.Expect(RecoveryMatrix.FindInconsistentOutcome(same) == null, "一致性：全部 reverted 应放行", RecoveryMatrix.Describe(same));
        }

        private static void ExpectRow(TestContext context, string name, RecoveryMatrix.Decision decision, int row, string outcome, string action)
        {
            bool ok = decision != null && decision.Row == row && decision.Outcome == outcome && decision.Action == action;
            string actual = decision == null
                ? "null"
                : decision.Row.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + decision.Outcome + "/" + decision.Action;
            context.Expect(ok, name + " → row" + row.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + outcome + "/" + action, actual);
        }

        /// <summary>
        /// §6.3 / §6.4 写侧：backup.json 与 journal 的原子写、seq 递增、无 BOM、完成判据。
        /// </summary>
        private static void TransactionStateCase(TestContext context)
        {
            context.Begin("transaction/state-io");

            string root = Path.Combine(Path.GetTempPath(), "CodexPluginSync-txn-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                CodexPaths paths = new CodexPaths(root);

                // --- journal：seq 递增 + 回读一致 + 无 BOM ---
                JournalDto journal = new JournalDto();
                journal.OperationId = "txn-test";
                journal.Phase = "planned";
                journal.Targets = new List<JournalTargetDto>();
                JournalStore.Write(paths, journal);
                context.Expect(journal.Seq == 1, "journal 首写 seq=1", journal.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture));

                journal.Phase = "staged";
                JournalStore.Write(paths, journal);
                context.Expect(journal.Seq == 2, "journal 二次写 seq=2", journal.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture));

                JournalDto reread = JournalStore.Read(paths);
                context.Expect(reread != null && reread.OperationId == "txn-test" && reread.Seq == 2 && reread.Phase == "staged",
                    "journal 回读 operationId/seq/phase 一致",
                    reread == null ? "null" : reread.OperationId + "/" + reread.Seq + "/" + reread.Phase);
                context.Expect(!AtomicJsonFile.HasBom(paths.JournalPath), "journal 文件不带 BOM", AtomicJsonFile.HasBom(paths.JournalPath).ToString());

                JournalStore.Delete(paths);
                context.Expect(!File.Exists(paths.JournalPath), "journal 可删除", File.Exists(paths.JournalPath).ToString());

                // --- backup.json：完成判据 ---
                string backupDirectory = Path.Combine(paths.BackupsRoot, "openai-bundled-sync-test");
                Directory.CreateDirectory(backupDirectory);

                BackupManifestDto emptyComplete = new BackupManifestDto();
                emptyComplete.Status = BackupStates.Complete;
                emptyComplete.Components = new List<BackupComponentDto>();
                context.Expect(BackupManifestStore.Validate(emptyComplete) != null,
                    "components 为空时 complete 必须被拒绝", "validate=null");

                BackupManifestDto missingDigest = new BackupManifestDto();
                missingDigest.Status = BackupStates.Complete;
                BackupComponentDto movedNoDigest = new BackupComponentDto();
                movedNoDigest.Name = "cache:browser";
                movedNoDigest.State = BackupStates.Moved;
                movedNoDigest.BackupPath = Path.Combine(backupDirectory, "cache-browser.previous");
                missingDigest.Components = new List<BackupComponentDto>();
                missingDigest.Components.Add(movedNoDigest);
                context.Expect(BackupManifestStore.Validate(missingDigest) != null,
                    "state=moved 且缺 previousDigest 必须被拒绝", "validate=null");

                movedNoDigest.PreviousDigest = new ComponentDigestDto();
                movedNoDigest.PreviousDigest.FileCount = 3;
                movedNoDigest.PreviousDigest.SizeBytes = 30;
                movedNoDigest.PreviousDigest.TreeHash = "a1b2c3";
                movedNoDigest.Digest = new ComponentDigestDto();
                movedNoDigest.Digest.FileCount = 4;
                movedNoDigest.Digest.SizeBytes = 40;
                movedNoDigest.Digest.TreeHash = "d4e5f6";
                context.Expect(BackupManifestStore.Validate(missingDigest) == null,
                    "完整快照应通过校验", BackupManifestStore.Validate(missingDigest));

                BackupComponentDto created = new BackupComponentDto();
                created.Name = "cache:chrome";
                created.State = BackupStates.Created;
                created.ExistedBefore = false;
                created.Digest = movedNoDigest.Digest;
                missingDigest.Components.Add(created);
                context.Expect(BackupManifestStore.Validate(missingDigest) == null,
                    "created 组件无需 backupPath/previousDigest", BackupManifestStore.Validate(missingDigest));

                // --- 写入并回读 ---
                missingDigest.BackupId = "20260101-000000";
                missingDigest.Kind = "sync";
                missingDigest.CreatedUtc = "2026-01-01T00:00:00Z";
                string manifestPath = BackupManifestStore.Write(backupDirectory, missingDigest);
                context.Expect(!AtomicJsonFile.HasBom(manifestPath), "backup.json 不带 BOM", AtomicJsonFile.HasBom(manifestPath).ToString());

                BackupManifestDto manifestReread = JsonRead.FromFile<BackupManifestDto>(manifestPath);
                context.Expect(manifestReread != null && manifestReread.Status == BackupStates.Complete &&
                    manifestReread.Components != null && manifestReread.Components.Count == 2,
                    "backup.json 回读 status/components 一致",
                    manifestReread == null ? "null" : manifestReread.Status + "/" + (manifestReread.Components == null ? "null" : manifestReread.Components.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                context.Expect(manifestReread != null && manifestReread.Components[0].PreviousDigest != null &&
                    manifestReread.Components[0].PreviousDigest.TreeHash == "a1b2c3",
                    "previousDigest 能原样回读",
                    manifestReread == null ? "null" : "n/a");
            }
            finally
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch (Exception)
                {
                }
            }
        }

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
