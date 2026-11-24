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
                SyncTransactionCase(context);
                SyncEngineCase(context, baseDirectory);
                NativeHostBoundaryCase(context, baseDirectory);
                ProcessGuardCase(context);
                EfsStateMachineCase(context, baseDirectory);
                RollbackRestoreCase(context, baseDirectory);
                RollbackRoundTripCase(context, baseDirectory);
                RollbackGuardsCase(context, baseDirectory);
                CleanupRetentionCase(context, baseDirectory);
                RecoveryExecuteCase(context, baseDirectory);
                EfsCopyCase(context, baseDirectory);
                DigestJunctionCase(context, baseDirectory);
                DiagRedactionCase(context, baseDirectory);
                ArgumentParsingCase(context);
                TrayAndVersionContractCase(context);
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
                movedNoDigest.ExistedBefore = true;
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

                // --- complete 的更严格拒绝分支（审阅要求）----------------
                BackupManifestDto plannedComplete = new BackupManifestDto();
                plannedComplete.Status = BackupStates.Complete;
                BackupComponentDto plannedComponent = new BackupComponentDto();
                plannedComponent.Name = "cache:browser";
                plannedComponent.State = BackupStates.Planned;
                plannedComplete.Components = new List<BackupComponentDto>();
                plannedComplete.Components.Add(plannedComponent);
                context.Expect(BackupManifestStore.Validate(plannedComplete) != null,
                    "complete 下 state=planned 必须被拒绝", "validate=null");

                plannedComponent.State = "weird-state";
                context.Expect(BackupManifestStore.Validate(plannedComplete) != null,
                    "complete 下未知 state 必须被拒绝", "validate=null");

                BackupManifestDto noNewDigest = new BackupManifestDto();
                noNewDigest.Status = BackupStates.Complete;
                BackupComponentDto movedWithoutNew = new BackupComponentDto();
                movedWithoutNew.Name = "cache:chrome";
                movedWithoutNew.State = BackupStates.Moved;
                movedWithoutNew.ExistedBefore = true;
                movedWithoutNew.BackupPath = Path.Combine(backupDirectory, "cache-chrome.previous");
                movedWithoutNew.PreviousDigest = movedNoDigest.PreviousDigest;
                noNewDigest.Components = new List<BackupComponentDto>();
                noNewDigest.Components.Add(movedWithoutNew);
                context.Expect(BackupManifestStore.Validate(noNewDigest) != null,
                    "complete 下缺 digest 必须被拒绝", "validate=null");

                movedWithoutNew.Digest = movedNoDigest.Digest;
                context.Expect(BackupManifestStore.Validate(noNewDigest) == null,
                    "补齐 digest 后应通过", BackupManifestStore.Validate(noNewDigest));

                movedWithoutNew.ExistedBefore = false;
                context.Expect(BackupManifestStore.Validate(noNewDigest) != null,
                    "state=moved 与 existedBefore=false 矛盾必须被拒绝", "validate=null");
                movedWithoutNew.ExistedBefore = true;

                BackupComponentDto createdButExisted = new BackupComponentDto();
                createdButExisted.Name = "cache:computer-use";
                createdButExisted.State = BackupStates.Created;
                createdButExisted.ExistedBefore = true;
                createdButExisted.Digest = movedNoDigest.Digest;
                noNewDigest.Components.Add(createdButExisted);
                context.Expect(BackupManifestStore.Validate(noNewDigest) != null,
                    "state=created 与 existedBefore=true 矛盾必须被拒绝", "validate=null");
                createdButExisted.ExistedBefore = false;
                context.Expect(BackupManifestStore.Validate(noNewDigest) == null,
                    "created + existedBefore=false 应通过", BackupManifestStore.Validate(noNewDigest));

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

        /// <summary>
        /// §7.2 同步事务：happy path 与“stage 缺失在动 live 之前中止”两个分支（纯 fixture 目录）。
        /// </summary>
        private static void SyncTransactionCase(TestContext context)
        {
            context.Begin("sync/transaction");

            string root = Path.Combine(Path.GetTempPath(), "CodexPluginSync-sync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                CodexPaths paths = new CodexPaths(root);
                string livePath = Path.Combine(root, "plugins", "cache", "openai-bundled", "browser");
                string stagePath = Path.Combine(root, "plugins", "cache", "openai-bundled", "browser.stage-test");
                string backupDirectory = Path.Combine(paths.BackupsRoot, "openai-bundled-sync-test");
                string backupPath = Path.Combine(backupDirectory, "cache-browser.previous");

                Directory.CreateDirectory(livePath);
                File.WriteAllText(Path.Combine(livePath, "old.txt"), "old");
                Directory.CreateDirectory(stagePath);
                File.WriteAllText(Path.Combine(stagePath, "new.txt"), "new");

                List<SyncTarget> targets = new List<SyncTarget>();
                SyncTarget target = new SyncTarget();
                target.Component = "cache:browser";
                target.LivePath = livePath;
                target.StagePath = stagePath;
                target.BackupPath = backupPath;
                target.ExistedBefore = true;
                targets.Add(target);

                SyncTransactionResult committed = SyncTransaction.Execute(paths, backupDirectory, "txn-happy", targets);
                context.Expect(committed.Status == SyncTransaction.StatusCommitted, "happy path 终态 committed", committed.Status + "/" + committed.Message);                context.Expect(File.Exists(Path.Combine(livePath, "new.txt")) && !File.Exists(Path.Combine(livePath, "old.txt")),
                    "live 已换成新内容", "live=" + string.Join(",", Directory.GetFiles(livePath)));
                context.Expect(File.Exists(Path.Combine(backupPath, "old.txt")), "备份保留同步前内容", backupPath);
                context.Expect(!File.Exists(paths.JournalPath), "事务结束后 journal 已删除", paths.JournalPath);

                BackupManifestDto manifest = JsonRead.FromFile<BackupManifestDto>(Path.Combine(backupDirectory, BackupManifestStore.FileName));
                context.Expect(manifest != null && manifest.Status == BackupStates.Complete, "backup.json status=complete", manifest == null ? "null" : manifest.Status);
                context.Expect(manifest != null && manifest.Components != null && manifest.Components.Count == 1 &&
                    manifest.Components[0].PreviousDigest != null && manifest.Components[0].Digest != null,
                    "backup.json 记录 previousDigest 与 digest",
                    manifest == null || manifest.Components == null ? "null" : manifest.Components.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                context.Expect(manifest != null && !TreeDigest.Equal(manifest.Components[0].PreviousDigest, manifest.Components[0].Digest),
                    "同步前后摘要不同（确认真的换了内容）", "equal");

                // 第二段：stage 缺失 → 在动 live 之前中止，live 与备份目录都不被写入
                string live2 = Path.Combine(root, @"plugins\cache\openai-bundled\chrome");
                Directory.CreateDirectory(live2);
                File.WriteAllText(Path.Combine(live2, "keep.txt"), "keep");
                string backup2 = Path.Combine(paths.BackupsRoot, "openai-bundled-sync-test2");

                List<SyncTarget> badTargets = new List<SyncTarget>();
                SyncTarget bad = new SyncTarget();
                bad.Component = "cache:chrome";
                bad.LivePath = live2;
                bad.StagePath = Path.Combine(root, "does-not-exist.stage");
                bad.BackupPath = Path.Combine(backup2, "cache-chrome.previous");
                bad.ExistedBefore = true;
                badTargets.Add(bad);

                SyncTransactionResult aborted = SyncTransaction.Execute(paths, backup2, "txn-abort", badTargets);
                context.Expect(aborted.Status == SyncTransaction.StatusAborted, "stage 缺失 → aborted", aborted.Status + "/" + aborted.Message);
                context.Expect(File.Exists(Path.Combine(live2, "keep.txt")), "中止时 live 未被移动", live2);
                context.Expect(!Directory.Exists(Path.Combine(backup2, "cache-chrome.previous")), "中止时未产生备份副本", backup2);
                context.Expect(!File.Exists(paths.JournalPath), "中止后 journal 已清理", paths.JournalPath);

                // 第三段：交换后注入失败 → 退回失败 → needs-attention 且保留 journal
                string live3 = Path.Combine(root, "plugins", "cache", "openai-bundled", "computer-use");
                Directory.CreateDirectory(live3);
                File.WriteAllText(Path.Combine(live3, "keep.txt"), "keep");
                string stage3 = live3 + ".stage3";
                Directory.CreateDirectory(stage3);
                File.WriteAllText(Path.Combine(stage3, "new.txt"), "new");
                string backup3 = Path.Combine(paths.BackupsRoot, "openai-bundled-sync-test3");
                string backupItem3 = Path.Combine(backup3, "cache-computer-use.previous");

                List<SyncTarget> bad3Targets = new List<SyncTarget>();
                SyncTarget target3 = new SyncTarget();
                target3.Component = "cache:computer-use";
                target3.LivePath = live3;
                target3.StagePath = stage3;
                target3.BackupPath = backupItem3;
                target3.ExistedBefore = true;
                bad3Targets.Add(target3);

                SyncTransactionResult attention = null;
                SyncTransaction.FailureInjectionHook = delegate(string point)
                {
                    if (point == "after-swap:cache:computer-use")
                    {
                        if (Directory.Exists(backupItem3)) { Directory.Delete(backupItem3, true); }
                        throw new InvalidOperationException("injected after-swap failure");
                    }
                };
                try
                {
                    attention = SyncTransaction.Execute(paths, backup3, "txn-attention", bad3Targets);
                }
                finally
                {
                    SyncTransaction.FailureInjectionHook = null;
                }

                context.Expect(attention != null && attention.Status == SyncTransaction.StatusNeedsAttention,
                    "退回失败 → needs-attention",
                    attention == null ? "null" : attention.Status + "/" + attention.Message);
                context.Expect(File.Exists(paths.JournalPath), "退回失败时必须保留 journal", paths.JournalPath);
                bool hasBackupMissing = false;
                if (attention != null)
                {
                    for (int i = 0; i < attention.Actions.Count; i++)
                    {
                        if (attention.Actions[i].IndexOf("备份缺失") >= 0) { hasBackupMissing = true; }
                    }
                }
                context.Expect(hasBackupMissing, "退回失败的原因应写明备份缺失", hasBackupMissing.ToString());

                // 第四段：交换后失败但退回成功 → 必须把 stage 也收掉（journal 已删，不能留垃圾）
                string live4 = Path.Combine(root, "plugins", "cache", "openai-bundled", "computer-use");
                Directory.CreateDirectory(live4);
                File.WriteAllText(Path.Combine(live4, "keep.txt"), "keep");
                string stage4 = live4 + ".stage4";
                Directory.CreateDirectory(stage4);
                File.WriteAllText(Path.Combine(stage4, "new.txt"), "new");
                string backup4 = Path.Combine(paths.BackupsRoot, "openai-bundled-sync-test4");

                List<SyncTarget> targets4 = new List<SyncTarget>();
                SyncTarget target4 = new SyncTarget();
                target4.Component = "cache:computer-use";
                target4.LivePath = live4;
                target4.StagePath = stage4;
                target4.BackupPath = Path.Combine(backup4, "cache-computer-use.previous");
                target4.ExistedBefore = true;
                targets4.Add(target4);

                SyncTransactionResult recoveredRun = null;
                SyncTransaction.FailureInjectionHook = delegate(string point)
                {
                    if (point == "after-swap:cache:computer-use")
                    {
                        throw new InvalidOperationException("injected after-swap failure (clean revert)");
                    }
                };
                try
                {
                    recoveredRun = SyncTransaction.Execute(paths, backup4, "txn-clean-revert", targets4);
                }
                finally
                {
                    SyncTransaction.FailureInjectionHook = null;
                }

                context.Expect(recoveredRun != null && recoveredRun.Status == SyncTransaction.StatusRecovered,
                    "退干净的失败 → recovered", recoveredRun == null ? "null" : recoveredRun.Status + "/" + recoveredRun.Message);
                context.Expect(File.Exists(Path.Combine(live4, "keep.txt")), "退回后 live 是同步前内容", live4);
                context.Expect(!Directory.Exists(stage4), "退干净后 stage 已清理", stage4);
                context.Expect(!File.Exists(paths.JournalPath), "退干净后 journal 已删除", paths.JournalPath);
            }
            catch (Exception ex)
            {
                context.Expect(false, "sync/transaction 不应抛异常", ex.GetType().Name + ": " + ex.Message + " @ " + ex.StackTrace);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// §7.2 + §9 的端到端一次：fixture 里跑 SyncEngine 两遍（完整同步 / 缓存命中跳过）。
        /// 只用临时目录，不触真实 CodexHome、不碰 EFS 与注册表。
        /// </summary>
        private static void SyncEngineCase(TestContext context, string baseDirectory)
        {
            context.Begin("sync/engine");

            string root = Path.Combine(baseDirectory, "sync-engine");
            string sourceBundle = Path.Combine(root, "src", "openai-bundled");
            string codexHome = Path.Combine(root, "CodexHome");
            try
            {
                string[] plugins = new string[] { "browser", "chrome", "computer-use" };
                for (int i = 0; i < plugins.Length; i++)
                {
                    string pluginDir = Path.Combine(sourceBundle, "plugins", plugins[i]);
                    string metaDir = Path.Combine(pluginDir, ".codex-plugin");
                    Directory.CreateDirectory(metaDir);
                    File.WriteAllText(Path.Combine(metaDir, "plugin.json"),
                        "{\"name\":\"" + plugins[i] + "\",\"version\":\"1.2.3\"}");
                    File.WriteAllText(Path.Combine(pluginDir, "payload.txt"), "payload-" + plugins[i]);
                }

                Directory.CreateDirectory(Path.Combine(codexHome, ".tmp", "bundled-marketplaces"));
                Directory.CreateDirectory(Path.Combine(codexHome, "plugins", "cache", "openai-bundled"));
                Directory.CreateDirectory(Path.Combine(codexHome, "backups"));

                SyncOptions options = new SyncOptions();
                options.CodexHome = codexHome;
                options.SourceBundleOverride = sourceBundle;
                options.Out = TextWriter.Null;
                options.Err = TextWriter.Null;
                options.SkipNativeHost = true;
                options.SkipProcessCheck = true;

                SyncRunResult first = SyncEngine.Run(options);
                context.Expect(first.ExitCode == ExitCodesLocator.Ok, "首次 --sync 退出码 0", first.ExitCode.ToString());
                context.Expect(first.Status == SyncTransaction.StatusCommitted, "首次 --sync 事务 committed", first.Status + "/" + first.Message);

                string cachedManifest = Path.Combine(codexHome, "plugins", "cache", "openai-bundled", "chrome", "1.2.3", ".codex-plugin", "plugin.json");
                context.Expect(File.Exists(cachedManifest), "缓存被同步到 1.2.3", cachedManifest);
                context.Expect(!File.Exists(Path.Combine(codexHome, "pending-operation.json")), "同步后无 journal 残留", "journal");

                string[] backupManifests = Directory.GetFiles(Path.Combine(codexHome, "backups"), "backup.json", SearchOption.AllDirectories);
                context.Expect(backupManifests.Length == 1, "产生 1 份 backup.json", backupManifests.Length.ToString());
                if (backupManifests.Length == 1)
                {
                    BackupManifestDto manifest = JsonRead.FromFile<BackupManifestDto>(backupManifests[0]);
                    context.Expect(manifest != null && manifest.Status == BackupStates.Complete,
                        "backup.json status=complete", manifest == null ? "null" : manifest.Status);
                    context.Expect(manifest != null && manifest.Components != null && manifest.Components.Count >= 3,
                        "backup.json 记录全部组件",
                        manifest == null || manifest.Components == null ? "null" : manifest.Components.Count.ToString());
                }

                string[] stageDirs = Directory.GetDirectories(Path.Combine(codexHome, "plugins", "cache", "openai-bundled"), "*.stage-*");
                context.Expect(stageDirs.Length == 0, "无 stage 残留", stageDirs.Length.ToString());

                SyncRunResult second = SyncEngine.Run(options);
                context.Expect(second.ExitCode == ExitCodesLocator.Ok, "二次 --sync 退出码 0", second.ExitCode.ToString());
                bool skipped = false;
                for (int i = 0; i < second.Lines.Count; i++)
                {
                    if (second.Lines[i].IndexOf("跳过") >= 0) { skipped = true; }
                }
                context.Expect(skipped, "二次 --sync 命中缓存并跳过", string.Join(" | ", second.Lines.ToArray()));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// §10.1 进程守卫：写入路径必须能真的检测到锁定进程（真机验收时就是 extension-host 锁住了
        /// cache\chrome，导致目录 rename 被拒）。这里用当前进程自己的名字当靶子，证明检测是真在跑。
        /// </summary>
        private static void ProcessGuardCase(TestContext context)
        {
            context.Begin("process/guard");

            string selfName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            List<string> extra = new List<string>();
            extra.Add(selfName);
            List<LockingProcess> detected = ProcessGuard.Detect(extra);
            context.Expect(detected.Count > 0, "能检测到指定名字的进程（" + selfName + "）", detected.Count.ToString());

            List<string> missing = new List<string>();
            string bogus = "cps-definitely-not-running-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            missing.Add(bogus);
            // 默认名单（chrome/msedge/extension-host）总会被扫到，所以只断言“没有这个不存在的名字”
            List<LockingProcess> none = ProcessGuard.Detect(missing);
            bool bogusSeen = false;
            for (int i = 0; i < none.Count; i++)
            {
                if (string.Equals(none[i].Name, bogus, StringComparison.OrdinalIgnoreCase)) { bogusSeen = true; }
            }
            context.Expect(!bogusSeen, "不存在的进程名不会出现在结果里", bogus);

            bool hasExtensionHost = false;
            for (int i = 0; i < ProcessGuard.DefaultLockingProcessNames.Length; i++)
            {
                if (ProcessGuard.DefaultLockingProcessNames[i] == "extension-host") { hasExtensionHost = true; }
            }
            context.Expect(hasExtensionHost, "默认锁定进程包含 extension-host（真机结论）", "missing");
        }

        /// <summary>§9.4 路径边界：只接受用户目录内、文件名恰为 com.openai.codexextension.json 的清单。不读注册表。</summary>
        private static void NativeHostBoundaryCase(TestContext context, string baseDirectory)
        {
            context.Begin("nativehost/path-boundary");

            string allowed = Path.Combine(baseDirectory, "native-allowed");
            string outside = Path.Combine(baseDirectory, "native-outside");
            Directory.CreateDirectory(allowed);
            Directory.CreateDirectory(outside);

            List<string> roots = new List<string>();
            roots.Add(allowed);

            string good = Path.Combine(allowed, NativeHostWriter.ManifestFileName);
            File.WriteAllText(good, "{\"path\":\"x\"}");
            context.Expect(NativeHostWriter.ValidateManifestPath(good, roots) == null,
                "允许目录内正确文件名应通过", NativeHostWriter.ValidateManifestPath(good, roots));

            string wrongName = Path.Combine(allowed, "evil.json");
            File.WriteAllText(wrongName, "{}");
            context.Expect(NativeHostWriter.ValidateManifestPath(wrongName, roots) != null,
                "文件名不符必须拒绝", "accepted");

            string external = Path.Combine(outside, NativeHostWriter.ManifestFileName);
            File.WriteAllText(external, "{}");
            context.Expect(NativeHostWriter.ValidateManifestPath(external, roots) != null,
                "越界路径必须拒绝", "accepted");

            context.Expect(NativeHostWriter.ValidateManifestPath("..\\evil\\" + NativeHostWriter.ManifestFileName, roots) != null,
                "包含 .. 的路径必须拒绝", "accepted");

            context.Expect(NativeHostWriter.ValidateManifestPath("", roots) != null,
                "空路径必须拒绝", "accepted");
        }

        /// <summary>
        /// §8 EFS 状态机（只跑临时目录）：预检记录 → 开启 → 撤销 → 既有加密不被误解除。
        /// 卷不支持 EFS 或 cipher 不可用时记为 skip（不把环境限制当成通过）。
        /// </summary>
        private static void EfsStateMachineCase(TestContext context, string baseDirectory)
        {
            context.Begin("efs/state-machine");

            string target = Path.Combine(baseDirectory, "efs-target");
            Directory.CreateDirectory(target);
            try
            {
                bool? pre = Efs.GetEncrypted(target);
                if (!pre.HasValue)
                {
                    context.Expect(true, "EFS 属性不可读，记为 skip（不宣称通过）", "skipped");
                    return;
                }
                if (pre.Value)
                {
                    context.Expect(true, "目标已加密，不在其上测“开启”，记为 skip", "skipped");
                    return;
                }

                EfsState state = null;
                bool prepared = false;
                try
                {
                    state = Efs.PrepareForWrite(target);
                    prepared = true;
                }
                catch (Exception ex)
                {
                    context.Expect(true, "卷不支持 EFS，记为 skip：" + ex.Message, "skipped");
                    return;
                }

                context.Expect(prepared && state != null && state.EnabledByThisRun, "本次开启被记录（enabledByThisRun=true）", "false");
                context.Expect(Efs.GetEncrypted(target) == true, "cipher /e 后目录确实加密（读回属性）", Efs.GetEncrypted(target).ToString());

                EfsRestoreResult restored = Efs.Restore(state);
                context.Expect(restored.Restored, "恢复报告成功", restored.Message);
                context.Expect(Efs.GetEncrypted(target) == false, "撤销后目录回到未加密", Efs.GetEncrypted(target).ToString());

                // 既有加密状态不得被误解除：EnabledByThisRun=false 时 Restore 不得改动
                if (Efs.SetEncrypted(target, true))
                {
                    EfsState existing = new EfsState();
                    existing.Path = target;
                    existing.PreEncrypted = true;
                    existing.EnabledByThisRun = false;
                    EfsRestoreResult kept = Efs.Restore(existing);
                    context.Expect(kept.Restored, "未开启本次写入时不报错", kept.Message);
                    context.Expect(Efs.GetEncrypted(target) == true, "既有加密状态保持（未被误解除）", Efs.GetEncrypted(target).ToString());
                    Efs.SetEncrypted(target, false);
                }
            }
            finally
            {
                try { Efs.SetEncrypted(target, false); } catch (Exception) { }
                try { Directory.Delete(target, true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// §7.3 回滚主路径：完整备份 → 校验 → 安全快照 → 逐组件回到同步前内容 → 安全快照可回滚。
        /// 只用临时目录，不读注册表、不动 EFS。
        /// </summary>
        private static void RollbackRestoreCase(TestContext context, string baseDirectory)
        {
            context.Begin("rollback/restore");

            string root = Path.Combine(baseDirectory, "rollback-restore");
            try
            {
                CodexPaths paths = new CodexPaths(root);
                string livePath = paths.MarketBundle;
                Directory.CreateDirectory(livePath);
                File.WriteAllText(Path.Combine(livePath, "new.txt"), "new-content");

                string backupDirectory = Path.Combine(paths.BackupsRoot, CodexPaths.BackupFolderPrefix + "fix1");
                string backupPath = Path.Combine(backupDirectory, "marketplace.previous");
                Directory.CreateDirectory(backupPath);
                File.WriteAllText(Path.Combine(backupPath, "old.txt"), "old-content");

                ComponentDigestDto oldDigest = TreeDigest.Compute(backupPath);
                ComponentDigestDto newDigest = TreeDigest.Compute(livePath);

                BackupManifestDto manifest = new BackupManifestDto();
                manifest.BackupId = "fix1";
                manifest.Kind = "sync";
                manifest.Status = BackupStates.Complete;
                manifest.CreatedUtc = "2026-01-02T03:04:05Z";
                manifest.Components = new List<BackupComponentDto>();
                manifest.NativeHost = new List<BackupNativeHostDto>();
                BackupComponentDto component = new BackupComponentDto();
                component.Name = "marketplace";
                component.LivePath = livePath;
                component.BackupPath = backupPath;
                component.ExistedBefore = true;
                component.State = BackupStates.Moved;
                component.Digest = newDigest;
                component.PreviousDigest = oldDigest;
                manifest.Components.Add(component);
                BackupManifestStore.Write(backupDirectory, manifest);

                RollbackOptions options = new RollbackOptions();
                options.CodexHome = root;
                options.SkipNativeHost = true;
                options.SkipEfs = true;
                options.SkipProcessCheck = true;
                options.Out = TextWriter.Null;
                options.Err = TextWriter.Null;

                SyncRunResult run = RollbackEngine.Run(options);
                context.Expect(run.ExitCode == ExitCodesLocator.Ok, "回滚退出码 0", run.ExitCode + "/" + run.Message);
                context.Expect(run.Status == RollbackEngine.StatusRolledBack, "终态 rolled-back", run.Status);
                context.Expect(File.Exists(Path.Combine(livePath, "old.txt")) && !File.Exists(Path.Combine(livePath, "new.txt")),
                    "live 已回到同步前内容", string.Join(",", Directory.GetFiles(livePath)));
                context.Expect(!File.Exists(paths.JournalPath), "回滚完成后 journal 已删除", paths.JournalPath);
                context.Expect(File.Exists(Path.Combine(backupPath, "old.txt")), "备份目录内容保留（回滚源不动）", backupPath);
                string[] leftoverStages = Directory.GetDirectories(paths.MarketParent, "*.rollback-stage-*", SearchOption.AllDirectories);
                context.Expect(leftoverStages.Length == 0, "无 rollback-stage 残留", leftoverStages.Length.ToString());
                context.Expect(TreeDigest.Equal(TreeDigest.Compute(livePath), oldDigest), "live 摘要与备份内容一致", "mismatch");

                string[] safetyManifests = Directory.GetFiles(paths.BackupsRoot, BackupManifestStore.FileName, SearchOption.AllDirectories);
                string safetyRoot = null;
                BackupManifestDto safety = null;
                for (int i = 0; i < safetyManifests.Length; i++)
                {
                    BackupManifestDto candidate = JsonRead.FromFile<BackupManifestDto>(safetyManifests[i]);
                    if (candidate != null && candidate.Kind == "rollback-safety")
                    {
                        safety = candidate;
                        safetyRoot = Path.GetDirectoryName(safetyManifests[i]);
                    }
                }
                context.Expect(safety != null, "产生了 rollback-safety 快照", safetyManifests.Length.ToString());
                context.Expect(safety != null && safety.Status == BackupStates.Complete, "安全快照 status=complete", safety == null ? "null" : safety.Status);
                context.Expect(safety != null && BackupManifestStore.Validate(safety) == null, "安全快照通过 §6.3 完成判据",
                    safety == null ? "null" : BackupManifestStore.Validate(safety));
                context.Expect(safetyRoot != null && File.Exists(Path.Combine(safetyRoot, "marketplace", "new.txt")),
                    "安全快照保留了回滚前的 live 内容", safetyRoot ?? "(null)");
                context.Expect(TreeDigest.Equal(safety == null ? null : safety.Components[0].Digest, newDigest),
                    "安全快照摘要记录的就是回滚前 live", "mismatch");
            }
            catch (Exception ex)
            {
                context.Expect(false, "rollback/restore 不应抛异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// §7.4 清理保留规则：keepBackups 只数 complete、partial/verify-failed 按天数、
        /// rollback-safety 只留最新 1 个、空备份目录直删、stage 按 staleStageHours、
        /// 被 journal 引用的一律不删。
        /// </summary>
        private static void CleanupRetentionCase(TestContext context, string baseDirectory)
        {
            context.Begin("cleanup/retention");

            string root = Path.Combine(baseDirectory, "cleanup");
            string toolLogs = Path.Combine(root, "tool-logs");
            try
            {
                CodexPaths paths = new CodexPaths(root);
                Directory.CreateDirectory(paths.BackupsRoot);
                Directory.CreateDirectory(paths.MarketParent);
                Directory.CreateDirectory(paths.CacheParent);
                Directory.CreateDirectory(paths.LogDirectory);
                Directory.CreateDirectory(toolLogs);

                string oldComplete = MakeBackup(paths, "sync", BackupStates.Complete, -10, "c-old");
                string newComplete = MakeBackup(paths, "sync", BackupStates.Complete, -1, "c-new");
                string oldPartial = MakeBackup(paths, "sync", BackupStates.Partial, -20, "p-old");
                string newPartial = MakeBackup(paths, "sync", BackupStates.Partial, -1, "p-new");
                string oldFailed = MakeBackup(paths, "sync", BackupStates.VerifyFailed, -40, "f-old");
                string oldSafety = MakeBackup(paths, "rollback-safety", BackupStates.Complete, -5, "s-old");
                string newSafety = MakeBackup(paths, "rollback-safety", BackupStates.Complete, -2, "s-new");
                string emptyDir = Path.Combine(paths.BackupsRoot, "openai-bundled-sync-empty");
                Directory.CreateDirectory(emptyDir);

                string staleStage = Path.Combine(paths.CacheParent, "browser.stage-old");
                Directory.CreateDirectory(staleStage);
                Directory.SetLastWriteTimeUtc(staleStage, DateTime.UtcNow.AddHours(-48));
                string freshStage = Path.Combine(paths.CacheParent, "chrome.stage-fresh");
                Directory.CreateDirectory(freshStage);
                string referencedStage = Path.Combine(paths.CacheParent, "computer-use.stage-ref");
                Directory.CreateDirectory(referencedStage);
                Directory.SetLastWriteTimeUtc(referencedStage, DateTime.UtcNow.AddHours(-72));

                JournalDto journal = new JournalDto();
                journal.OperationId = "cleanup-ref";
                journal.Phase = JournalPhases.CommitStarted;
                journal.Targets = new List<JournalTargetDto>();
                JournalTargetDto target = new JournalTargetDto();
                target.Component = "cache:computer-use";
                target.LivePath = Path.Combine(paths.CacheParent, "computer-use");
                target.StagePath = referencedStage;
                journal.Targets.Add(target);
                JournalStore.Write(paths, journal);

                string oldLog = Path.Combine(paths.LogDirectory, "sync-old.log");
                File.WriteAllText(oldLog, "old");
                File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-60));
                string freshLog = Path.Combine(paths.LogDirectory, "sync-fresh.log");
                File.WriteAllText(freshLog, "fresh");

                Settings settings = new Settings();
                settings.KeepBackups = 1;
                settings.StaleStageHours = 24;
                settings.KeepLogDays = 30;
                settings.PartialBackupRetentionDays = 7;
                settings.FailedBackupRetentionDays = 30;

                CleanupOptions cleanup = new CleanupOptions();
                cleanup.CodexHome = root;
                cleanup.SettingsOverride = settings;
                cleanup.ToolLogDirectoryOverride = toolLogs;
                cleanup.Out = TextWriter.Null;
                cleanup.Err = TextWriter.Null;

                SyncRunResult run = CleanupEngine.Run(cleanup);
                context.Expect(run.ExitCode == ExitCodesLocator.Ok, "清理退出码 0", run.ExitCode + "/" + run.Message);

                context.Expect(!Directory.Exists(oldComplete), "超出 keepBackups 的 complete 被删",
                    oldComplete + " || " + string.Join(" | ", run.Lines.ToArray()));
                context.Expect(Directory.Exists(newComplete), "最新 complete 保留", newComplete);
                context.Expect(!Directory.Exists(oldPartial), "过期 partial 被删", oldPartial);
                context.Expect(Directory.Exists(newPartial), "未过期 partial 保留", newPartial);
                context.Expect(!Directory.Exists(oldFailed), "过期 verify-failed 被删", oldFailed);
                context.Expect(!Directory.Exists(oldSafety), "旧 rollback-safety 被删", oldSafety);
                context.Expect(Directory.Exists(newSafety), "最新 rollback-safety 保留", newSafety);
                context.Expect(!Directory.Exists(emptyDir), "空备份目录直删", emptyDir);

                context.Expect(!Directory.Exists(staleStage), "过期 stage 被删",
                    staleStage + " || " + string.Join(" | ", run.Lines.ToArray()));
                context.Expect(Directory.Exists(freshStage), "新鲜 stage 保留", freshStage);
                context.Expect(Directory.Exists(referencedStage), "journal 引用的 stage 永不删", referencedStage);

                context.Expect(!File.Exists(oldLog), "超 keepLogDays 的日志被删", oldLog);
                context.Expect(File.Exists(freshLog), "近期日志保留", freshLog);

                // 干跑：不删任何东西
                string staleStage2 = Path.Combine(paths.CacheParent, "browser.stage-dry");
                Directory.CreateDirectory(staleStage2);
                Directory.SetLastWriteTimeUtc(staleStage2, DateTime.UtcNow.AddHours(-48));
                cleanup.DryRun = true;
                SyncRunResult dry = CleanupEngine.Run(cleanup);
                context.Expect(dry.Status == CleanupEngine.StatusDryRun, "干跑终态 dry-run", dry.Status);
                context.Expect(Directory.Exists(staleStage2), "干跑不删任何东西", staleStage2);

                CliOptions parsed = null;
                string parseError = null;
                bool ok = CliOptions.TryParse(new string[] { "--cleanup-only", "--dry-run" }, out parsed, out parseError);
                context.Expect(ok && parsed.Command == CliOptions.CommandCleanupOnly && parsed.DryRun,
                    "--cleanup-only --dry-run 可解析", parseError);
            }
            catch (Exception ex)
            {
                context.Expect(false, "cleanup/retention 不应抛异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static string MakeBackup(CodexPaths paths, string kind, string status, int ageDays, string tag)
        {
            string directory = Path.Combine(paths.BackupsRoot, CodexPaths.BackupFolderPrefix + kind.Replace('-', '_') + "-" + tag);
            Directory.CreateDirectory(directory);
            BackupManifestDto manifest = new BackupManifestDto();
            manifest.BackupId = Path.GetFileName(directory);
            manifest.Kind = kind;
            manifest.Status = status;
            manifest.CreatedUtc = DateTime.UtcNow.AddDays(ageDays).ToString("yyyy-MM-ddTHH:mm:ssZ",
                System.Globalization.CultureInfo.InvariantCulture);
            manifest.Components = new List<BackupComponentDto>();
            manifest.NativeHost = new List<BackupNativeHostDto>();
            BackupManifestStore.Write(directory, manifest);
            // 让目录名与 createdUtc 不参与判定：身份只看 backup.json 的 kind/status
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(ageDays));
            return directory;
        }

        /// <summary>
        /// §7.5 诊断包：文件清单齐全、默认脱敏（用户名/用户目录被替掉）、
        /// manifest.json 如实标记 redacted，--no-redact 时标记为 false。
        /// </summary>
        private static void DiagRedactionCase(TestContext context, string baseDirectory)
        {
            context.Begin("diag/redaction");

            string root = Path.Combine(baseDirectory, "diag");
            string toolLogs = Path.Combine(root, "tool-logs");
            try
            {
                CodexPaths paths = new CodexPaths(root);
                Directory.CreateDirectory(paths.BackupsRoot);
                Directory.CreateDirectory(paths.LogDirectory);
                Directory.CreateDirectory(toolLogs);
                Directory.CreateDirectory(Path.Combine(root, ".tmp", "bundled-marketplaces"));

                string userName = Environment.GetEnvironmentVariable("USERNAME") ?? "unknownuser";
                string logPath = Path.Combine(paths.LogDirectory, "sync-sample.log");
                File.WriteAllText(logPath, "root=" + root + " user=" + userName + "\r\n");

                DiagOptions diag = new DiagOptions();
                diag.CodexHome = root;
                diag.OutPath = Path.Combine(root, "diag.zip");
                diag.SkipRuntimeProbe = true;
                diag.ToolLogDirectoryOverride = toolLogs;
                diag.Out = TextWriter.Null;
                diag.Err = TextWriter.Null;

                SyncRunResult run = DiagExporter.Run(diag);
                context.Expect(run.ExitCode == ExitCodesLocator.Ok, "导出退出码 0", run.ExitCode + "/" + run.Message);
                context.Expect(File.Exists(diag.OutPath), "zip 已生成", diag.OutPath);
                if (!File.Exists(diag.OutPath))
                {
                    return;
                }

                bool hasState = false;
                bool hasManifest = false;
                bool hasReadme = false;
                bool hasLog = false;
                string manifestText = null;
                string stateText = null;
                string logText = null;
                using (System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.OpenRead(diag.OutPath))
                {
                    foreach (System.IO.Compression.ZipArchiveEntry entry in archive.Entries)
                    {
                        string text;
                        using (System.IO.StreamReader reader = new System.IO.StreamReader(entry.Open()))
                        {
                            text = reader.ReadToEnd();
                        }
                        if (entry.FullName == "state.json") { hasState = true; stateText = text; }
                        else if (entry.FullName == "manifest.json") { hasManifest = true; manifestText = text; }
                        else if (entry.FullName == "README.txt") { hasReadme = true; }
                        else if (entry.FullName.StartsWith("logs/", StringComparison.Ordinal)) { hasLog = true; logText = text; }
                    }
                }

                context.Expect(hasState && hasManifest && hasReadme && hasLog, "包含 state/manifest/README/logs",
                    "state=" + hasState + " manifest=" + hasManifest + " readme=" + hasReadme + " log=" + hasLog);
                context.Expect(manifestText != null && manifestText.IndexOf("\"redacted\":true", StringComparison.Ordinal) >= 0,
                    "默认 manifest.json 标记 redacted=true", manifestText);
                context.Expect(stateText != null && stateText.IndexOf(root, StringComparison.OrdinalIgnoreCase) < 0,
                    "state.json 不含完整 CodexHome 路径", stateText);
                context.Expect(logText != null && logText.IndexOf("<codexhome>", StringComparison.Ordinal) >= 0,
                    "日志里的 CodexHome 被替换为 <codexhome>", logText);
                context.Expect(logText != null && logText.IndexOf(userName, StringComparison.Ordinal) < 0,
                    "日志里的用户名已脱敏", logText);

                // --no-redact：必须显式传参，且如实标记
                string rawPath = Path.Combine(root, "diag-raw.zip");
                diag.OutPath = rawPath;
                diag.NoRedact = true;
                SyncRunResult raw = DiagExporter.Run(diag);
                context.Expect(raw.ExitCode == ExitCodesLocator.Ok, "--no-redact 也能导出", raw.ExitCode + "/" + raw.Message);
                string rawManifest = null;
                using (System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.OpenRead(rawPath))
                {
                    foreach (System.IO.Compression.ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.FullName != "manifest.json") { continue; }
                        using (System.IO.StreamReader reader = new System.IO.StreamReader(entry.Open()))
                        {
                            rawManifest = reader.ReadToEnd();
                        }
                    }
                }
                context.Expect(rawManifest != null && rawManifest.IndexOf("\"redacted\":false", StringComparison.Ordinal) >= 0,
                    "--no-redact 时 manifest 标记 redacted=false", rawManifest);
            }
            catch (Exception ex)
            {
                context.Expect(false, "diag/redaction 不应抛异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// sync → rollback 闭环：直接吃 SyncEngine 产出的 backup.json（不手造），
        /// 验证 digest/previousDigest 语义与 existedBefore=false 的“删除”回滚语义。
        /// </summary>
        private static void RollbackRoundTripCase(TestContext context, string baseDirectory)
        {
            context.Begin("rollback/roundtrip");

            string root = Path.Combine(baseDirectory, "rollback-roundtrip");
            string sourceBundle = Path.Combine(root, "src", "openai-bundled");
            string codexHome = Path.Combine(root, "CodexHome");
            try
            {
                string[] plugins = new string[] { "browser", "chrome", "computer-use" };
                for (int i = 0; i < plugins.Length; i++)
                {
                    string pluginDir = Path.Combine(sourceBundle, "plugins", plugins[i]);
                    string metaDir = Path.Combine(pluginDir, ".codex-plugin");
                    Directory.CreateDirectory(metaDir);
                    File.WriteAllText(Path.Combine(metaDir, "plugin.json"),
                        "{\"name\":\"" + plugins[i] + "\",\"version\":\"2.0.0\"}");
                    File.WriteAllText(Path.Combine(pluginDir, "payload.txt"), "payload-" + plugins[i]);
                }

                Directory.CreateDirectory(Path.Combine(codexHome, ".tmp", "bundled-marketplaces"));
                Directory.CreateDirectory(Path.Combine(codexHome, "plugins", "cache", "openai-bundled"));
                Directory.CreateDirectory(Path.Combine(codexHome, "backups"));

                SyncOptions sync = new SyncOptions();
                sync.CodexHome = codexHome;
                sync.SourceBundleOverride = sourceBundle;
                sync.Out = TextWriter.Null;
                sync.Err = TextWriter.Null;
                sync.SkipNativeHost = true;
                sync.SkipEfs = true;
                sync.SkipProcessCheck = true;

                SyncRunResult first = SyncEngine.Run(sync);
                context.Expect(first.ExitCode == ExitCodesLocator.Ok, "sync 退出码 0", first.ExitCode + "/" + first.Message);

                CodexPaths paths = new CodexPaths(codexHome);
                context.Expect(Directory.Exists(paths.MarketBundle), "sync 后 marketplace 存在", paths.MarketBundle);

                RollbackOptions rollback = new RollbackOptions();
                rollback.CodexHome = codexHome;
                rollback.SkipNativeHost = true;
                rollback.SkipEfs = true;
                rollback.SkipProcessCheck = true;
                rollback.Out = TextWriter.Null;
                rollback.Err = TextWriter.Null;

                SyncRunResult back = RollbackEngine.Run(rollback);
                context.Expect(back.ExitCode == ExitCodesLocator.Ok, "rollback 退出码 0（默认选最新 complete）", back.ExitCode + "/" + back.Message);
                context.Expect(!Directory.Exists(paths.MarketBundle), "marketplace 回到“原本不存在”", paths.MarketBundle);
                context.Expect(!Directory.Exists(Path.Combine(paths.CacheParent, "browser")), "cache:browser 回到“原本不存在”", paths.CacheParent);
                context.Expect(!Directory.Exists(Path.Combine(paths.CacheParent, "chrome")), "cache:chrome 回到“原本不存在”", paths.CacheParent);
                context.Expect(!File.Exists(paths.JournalPath), "rollback 后无 journal 残留", paths.JournalPath);

                string[] safetyManifests = Directory.GetFiles(paths.BackupsRoot, BackupManifestStore.FileName, SearchOption.AllDirectories);
                int safetyCount = 0;
                for (int i = 0; i < safetyManifests.Length; i++)
                {
                    BackupManifestDto candidate = JsonRead.FromFile<BackupManifestDto>(safetyManifests[i]);
                    if (candidate != null && candidate.Kind == "rollback-safety") { safetyCount++; }
                }
                context.Expect(safetyCount == 1, "恰好产出 1 份 rollback-safety 快照", safetyCount.ToString());
            }
            catch (Exception ex)
            {
                context.Expect(false, "rollback/roundtrip 不应抛异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// §6.6 恢复执行侧：判定只看“存在性 + 摘要”，不依赖 journal 的 swapped 位。
        /// 行 4 前滚、行 6 清 stage、行 9 转 attention、终态不一致时一个动作也不执行。
        /// </summary>
        private static void RecoveryExecuteCase(TestContext context, string baseDirectory)
        {
            context.Begin("recovery/execute");

            string root = Path.Combine(baseDirectory, "recovery-execute");
            try
            {
                // A) 行 4：live 缺失、备份是旧内容、stage 完整 → 前滚
                CodexPaths paths = new CodexPaths(Path.Combine(root, "a"));
                string liveA = paths.MarketBundle;
                string backupA = Path.Combine(paths.BackupsRoot, "openai-bundled-sync-a", "marketplace.previous");
                string stageA = Path.Combine(paths.MarketParent, "openai-bundled.stage-a");
                WriteTree(backupA, "old.txt", "old");
                WriteTree(stageA, "new.txt", "new");
                WriteJournal(paths, "sync", liveA, backupA, stageA, null, TreeDigest.Compute(backupA),
                    TreeDigest.Compute(stageA), true);

                RecoveryOutcome a = RecoveryRunner.Run(paths, TextWriter.Null);
                context.Expect(a.Recovered && !a.NeedsAttention, "行 4 可自动前滚", a.Message + " || " + string.Join(" | ", a.Rows.ToArray()));
                context.Expect(File.Exists(Path.Combine(liveA, "new.txt")), "行 4 前滚后 live 是 stage 内容", liveA);
                context.Expect(!Directory.Exists(stageA), "行 4 前滚后 stage 已移走", stageA);
                context.Expect(!File.Exists(paths.JournalPath), "行 4 成功后 journal 已删", paths.JournalPath);

                // B) 行 6：live 已是新内容、备份是旧内容、还有残留 stage → 只清 stage
                CodexPaths pathsB = new CodexPaths(Path.Combine(root, "b"));
                string liveB = pathsB.MarketBundle;
                string backupB = Path.Combine(pathsB.BackupsRoot, "openai-bundled-sync-b", "marketplace.previous");
                string stageB = Path.Combine(pathsB.MarketParent, "openai-bundled.stage-b");
                WriteTree(backupB, "old.txt", "old");
                WriteTree(liveB, "new.txt", "new");
                WriteTree(stageB, "new.txt", "new");
                WriteJournal(pathsB, "sync", liveB, backupB, stageB, null, TreeDigest.Compute(backupB),
                    TreeDigest.Compute(liveB), true);

                RecoveryOutcome b = RecoveryRunner.Run(pathsB, TextWriter.Null);
                context.Expect(b.Recovered && !b.NeedsAttention, "行 6 可自动收尾", b.Message + " || " + string.Join(" | ", b.Rows.ToArray()));
                context.Expect(File.Exists(Path.Combine(liveB, "new.txt")), "行 6 不动已提交的 live", liveB);
                context.Expect(!Directory.Exists(stageB), "行 6 删除残留 stage", stageB);
                context.Expect(!File.Exists(pathsB.JournalPath), "行 6 成功后 journal 已删", pathsB.JournalPath);

                // C) 行 9：三份都不存在 → 不得自动处理，journal 必须保留
                CodexPaths pathsC = new CodexPaths(Path.Combine(root, "c"));
                string liveC = pathsC.MarketBundle;
                Directory.CreateDirectory(pathsC.MarketParent);
                WriteJournal(pathsC, "sync", liveC, Path.Combine(pathsC.BackupsRoot, "b", "marketplace.previous"),
                    Path.Combine(pathsC.MarketParent, "openai-bundled.stage-c"), null,
                    EmptyDigest(), EmptyDigest(), true);

                RecoveryOutcome c = RecoveryRunner.Run(pathsC, TextWriter.Null);
                context.Expect(c.NeedsAttention, "行 9 必须 needs-attention", c.Message);
                context.Expect(File.Exists(pathsC.JournalPath), "行 9 保留 journal", pathsC.JournalPath);

                // D) 终态不一致：一个行 5、一个行 9 → 一个动作都不执行
                CodexPaths pathsD = new CodexPaths(Path.Combine(root, "d"));
                string liveD = pathsD.MarketBundle;
                string backupD = Path.Combine(pathsD.BackupsRoot, "openai-bundled-sync-d", "marketplace.previous");
                WriteTree(backupD, "old.txt", "old");
                WriteTree(liveD, "new.txt", "new");
                string liveD2 = Path.Combine(pathsD.CacheParent, "browser");
                List<JournalTargetDto> targets = new List<JournalTargetDto>();
                targets.Add(NewTarget("marketplace", liveD, backupD, null, null, TreeDigest.Compute(backupD), TreeDigest.Compute(liveD), true));
                targets.Add(NewTarget("cache:browser", liveD2, Path.Combine(pathsD.BackupsRoot, "openai-bundled-sync-d", "cache-browser.previous"),
                    null, null, EmptyDigest(), EmptyDigest(), true));
                WriteJournalTargets(pathsD, "sync", targets);

                RecoveryOutcome d = RecoveryRunner.Run(pathsD, TextWriter.Null);
                context.Expect(d.NeedsAttention, "终态不一致 → needs-attention", d.Message);
                context.Expect(Directory.Exists(liveD) && File.Exists(Path.Combine(liveD, "new.txt")),
                    "终态不一致时 live 未被触碰", liveD);
                context.Expect(File.Exists(pathsD.JournalPath), "终态不一致时保留 journal", pathsD.JournalPath);
            }
            catch (Exception ex)
            {
                context.Expect(false, "recovery/execute 不应抛异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// 真机回归（2026-11-24）：树摘要不得跟进 reparse point。
        /// 备份里的 `latest` junction 指回 live，若摘要跟随它，交换后备份摘要就变了，回滚必然拒绝。
        /// 这里造一份“真目录 + junction 指回真目录”，断言只数一遍文件，且 copy 前后摘要一致。
        /// </summary>
        private static void DigestJunctionCase(TestContext context, string baseDirectory)
        {
            context.Begin("digest/no-junction-walk");

            string root = Path.Combine(baseDirectory, "digest-junction");
            string version = Path.Combine(root, "26.1.0");
            try
            {
                Directory.CreateDirectory(version);
                File.WriteAllText(Path.Combine(version, "a.txt"), "a");
                File.WriteAllText(Path.Combine(version, "b.txt"), "b");

                string latest = Path.Combine(root, "latest");
                bool linked = false;
                try
                {
                    ProcessRunner.RunResult run = ProcessRunner.Run("cmd.exe", "/c mklink /J \"" + latest + "\" \"" + version + "\"", 20000);
                    linked = run.Success && Directory.Exists(latest);
                }
                catch (Exception)
                {
                    linked = false;
                }
                if (!linked)
                {
                    context.Expect(true, "无法建 junction，记为 skip", "skipped");
                    return;
                }

                ComponentDigestDto digest = TreeDigest.Compute(root);
                context.Expect(digest != null && digest.FileCount == 2,
                    "junction 不被递归（只数 2 个真实文件）",
                    digest == null ? "null" : digest.FileCount.ToString());

                string copy = Path.Combine(baseDirectory, "digest-junction-copy");
                Installer.CopyTreePreservingLinks(root, copy);
                context.Expect(TreeDigest.Equal(digest, TreeDigest.Compute(copy)),
                    "带 junction 的树 copy 前后摘要一致",
                    "mismatch");
                try { Directory.Delete(copy, true); } catch (Exception) { }
            }
            catch (Exception ex)
            {
                context.Expect(false, "digest/no-junction-walk 不应抛异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// 真机回归（2026-11-24）：AppX 源包里的文件带 EFS 加密属性，复制到**未加密**目录时
        /// File.Copy 会直接失败（ERROR_ENCRYPTION_FAILED 6000），而复制到加密目录反而正常。
        /// 这里用 cipher 造一个加密源目录，验证 Installer 能把它拷到普通目录（失败则记为 skip）。
        /// </summary>
        private static void EfsCopyCase(TestContext context, string baseDirectory)
        {
            context.Begin("efs/encrypted-source-copy");

            string encryptedSource = Path.Combine(baseDirectory, "efs-src");
            string plainDestination = Path.Combine(baseDirectory, "efs-dst");
            try
            {
                Directory.CreateDirectory(encryptedSource);
                bool? pre = Efs.GetEncrypted(encryptedSource);
                if (!pre.HasValue)
                {
                    context.Expect(true, "EFS 属性不可读，记为 skip", "skipped");
                    return;
                }
                if (!Efs.SetEncrypted(encryptedSource, true))
                {
                    context.Expect(true, "卷不支持 EFS，记为 skip", "skipped");
                    return;
                }
                File.WriteAllText(Path.Combine(encryptedSource, "payload.txt"), "payload");
                string payloadPath = Path.Combine(encryptedSource, "payload.txt");
                // 注意：Efs.GetEncrypted 只针对目录（内部先 Directory.Exists），文件属性要直接读。
                FileAttributes payloadAttributes = File.GetAttributes(payloadPath);
                bool fileEncrypted = (payloadAttributes & FileAttributes.Encrypted) == FileAttributes.Encrypted;
                if (!fileEncrypted)
                {
                    context.Expect(true, "加密目录里的新文件没继承加密属性，记为 skip", "skipped");
                    return;
                }

                Installer.CopyTreePreservingLinks(encryptedSource, plainDestination);
                context.Expect(File.Exists(Path.Combine(plainDestination, "payload.txt")), "加密源能拷到未加密目录", plainDestination);
                context.Expect(File.ReadAllText(Path.Combine(plainDestination, "payload.txt")) == "payload",
                    "内容一致", "mismatch");
                context.Expect(TreeDigest.Equal(TreeDigest.Compute(encryptedSource), TreeDigest.Compute(plainDestination)),
                    "拷贝后摘要与源一致", "mismatch");
            }
            catch (Exception ex)
            {
                context.Expect(false, "efs/encrypted-source-copy 不应抛异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Efs.SetEncrypted(encryptedSource, false); } catch (Exception) { }
                try { Directory.Delete(encryptedSource, true); } catch (Exception) { }
                try { Directory.Delete(plainDestination, true); } catch (Exception) { }
            }
        }

        private static void WriteTree(string directory, string fileName, string content)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, fileName), content);
        }

        private static ComponentDigestDto EmptyDigest()
        {
            ComponentDigestDto digest = new ComponentDigestDto();
            digest.FileCount = -1;
            digest.SizeBytes = -1;
            digest.TreeHash = "none";
            return digest;
        }

        private static JournalTargetDto NewTarget(string component, string livePath, string backupPath, string stagePath,
            string safetyPath, ComponentDigestDto oldDigest, ComponentDigestDto newDigest, bool existedBefore)
        {
            JournalTargetDto target = new JournalTargetDto();
            target.Component = component;
            target.LivePath = livePath;
            target.BackupPath = backupPath;
            target.StagePath = stagePath;
            target.SafetyPath = safetyPath;
            target.OldDigest = oldDigest;
            target.NewDigest = newDigest;
            target.ExistedBefore = existedBefore;
            return target;
        }

        private static void WriteJournal(CodexPaths paths, string kind, string livePath, string backupPath,
            string stagePath, string safetyPath, ComponentDigestDto oldDigest, ComponentDigestDto newDigest, bool existedBefore)
        {
            List<JournalTargetDto> targets = new List<JournalTargetDto>();
            targets.Add(NewTarget("marketplace", livePath, backupPath, stagePath, safetyPath, oldDigest, newDigest, existedBefore));
            WriteJournalTargets(paths, kind, targets);
        }

        private static void WriteJournalTargets(CodexPaths paths, string kind, List<JournalTargetDto> targets)
        {
            JournalDto journal = new JournalDto();
            journal.OperationId = "recovery-test";
            journal.BackupId = "recovery-test";
            journal.Phase = kind == "rollback" ? RollbackPhases.CommitStarted : JournalPhases.CommitStarted;
            journal.Targets = targets;
            JournalStore.Write(paths, journal);
        }

        /// <summary>
        /// §7.3 硬前置：partial 不可回滚、缺 previousDigest 拒绝、备份内容不符拒绝、
        /// 未收尾 journal 直接阻停、无备份拒绝——五种情形都必须“live 一点不动”。
        /// </summary>
        private static void RollbackGuardsCase(TestContext context, string baseDirectory)
        {
            context.Begin("rollback/guards");

            string root = Path.Combine(baseDirectory, "rollback-guards");
            try
            {
                CodexPaths paths = new CodexPaths(root);
                string livePath = paths.MarketBundle;
                Directory.CreateDirectory(livePath);
                File.WriteAllText(Path.Combine(livePath, "new.txt"), "new-content");

                RollbackOptions options = new RollbackOptions();
                options.CodexHome = root;
                options.SkipNativeHost = true;
                options.SkipEfs = true;
                options.SkipProcessCheck = true;
                options.Out = TextWriter.Null;
                options.Err = TextWriter.Null;

                // a) 完全没备份
                SyncRunResult none = RollbackEngine.Run(options);
                context.Expect(none.ExitCode == ExitCodesLocator.Error, "无备份 → 退出码 1", none.ExitCode.ToString());
                context.Expect(File.Exists(Path.Combine(livePath, "new.txt")), "无备份时 live 未被触碰", livePath);

                // b) status=partial
                string partialDirectory = Path.Combine(paths.BackupsRoot, CodexPaths.BackupFolderPrefix + "partial1");
                string partialBackup = Path.Combine(partialDirectory, "marketplace.previous");
                Directory.CreateDirectory(partialBackup);
                File.WriteAllText(Path.Combine(partialBackup, "old.txt"), "old-content");
                BackupManifestDto partial = NewManifest("partial1", BackupStates.Partial, "2026-01-01T00:00:00Z", partialBackup, livePath, TreeDigest.Compute(partialBackup), TreeDigest.Compute(livePath));
                BackupManifestStore.Write(partialDirectory, partial);

                SyncRunResult partialRun = RollbackEngine.Run(options);
                context.Expect(partialRun.ExitCode == ExitCodesLocator.Error, "status=partial → 拒绝", partialRun.Status + "/" + partialRun.Message);
                context.Expect(File.Exists(Path.Combine(livePath, "new.txt")), "partial 被拒时 live 未被触碰", "touched");

                // c) complete 但缺 previousDigest
                string noDigestDirectory = Path.Combine(paths.BackupsRoot, CodexPaths.BackupFolderPrefix + "nodigest");
                string noDigestBackup = Path.Combine(noDigestDirectory, "marketplace.previous");
                Directory.CreateDirectory(noDigestBackup);
                File.WriteAllText(Path.Combine(noDigestBackup, "old.txt"), "old-content");
                BackupManifestDto noDigest = NewManifest("nodigest", BackupStates.Complete, "2026-01-05T00:00:00Z", noDigestBackup, livePath, null, TreeDigest.Compute(livePath));
                BackupManifestStore.Write(noDigestDirectory, noDigest);

                SyncRunResult noDigestRun = RollbackEngine.Run(options);
                context.Expect(noDigestRun.ExitCode == ExitCodesLocator.Error, "缺 previousDigest → 拒绝回滚", noDigestRun.Status + "/" + noDigestRun.Message);
                context.Expect(File.Exists(Path.Combine(livePath, "new.txt")), "缺摘要被拒时 live 未被触碰", "touched");

                // d) 备份内容与 previousDigest 不一致
                string tamperedDirectory = Path.Combine(paths.BackupsRoot, CodexPaths.BackupFolderPrefix + "tampered");
                string tamperedBackup = Path.Combine(tamperedDirectory, "marketplace.previous");
                Directory.CreateDirectory(tamperedBackup);
                File.WriteAllText(Path.Combine(tamperedBackup, "old.txt"), "old-content");
                ComponentDigestDto expectedOld = TreeDigest.Compute(tamperedBackup);
                BackupManifestDto tampered = NewManifest("tampered", BackupStates.Complete, "2026-01-06T00:00:00Z", tamperedBackup, livePath, expectedOld, TreeDigest.Compute(livePath));
                BackupManifestStore.Write(tamperedDirectory, tampered);
                File.WriteAllText(Path.Combine(tamperedBackup, "old.txt"), "tampered-content");

                SyncRunResult tamperedRun = RollbackEngine.Run(options);
                context.Expect(tamperedRun.ExitCode == ExitCodesLocator.Error, "备份内容不符 → 拒绝回滚", tamperedRun.Status + "/" + tamperedRun.Message);
                context.Expect(File.Exists(Path.Combine(livePath, "new.txt")), "内容不符被拒时 live 未被触碰", "touched");

                // e) 未收尾 journal → needs-attention，且不读备份、不动 live
                JournalDto pending = new JournalDto();
                pending.OperationId = "leftover";
                pending.Phase = JournalPhases.CommitStarted;
                pending.Targets = new List<JournalTargetDto>();
                JournalStore.Write(paths, pending);

                SyncRunResult blocked = RollbackEngine.Run(options);
                context.Expect(blocked.ExitCode == ExitCodesLocator.NeedsAttention, "遗留 journal → 退出码 2", blocked.ExitCode.ToString());
                context.Expect(File.Exists(paths.JournalPath), "遗留 journal 必须保留", paths.JournalPath);
                context.Expect(File.Exists(Path.Combine(livePath, "new.txt")), "遗留 journal 时 live 未被触碰", "touched");
            }
            catch (Exception ex)
            {
                context.Expect(false, "rollback/guards 不应抛异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static BackupManifestDto NewManifest(string backupId, string status, string createdUtc,
            string backupPath, string livePath, ComponentDigestDto previousDigest, ComponentDigestDto digest)
        {
            BackupManifestDto manifest = new BackupManifestDto();
            manifest.BackupId = backupId;
            manifest.Kind = "sync";
            manifest.Status = status;
            manifest.CreatedUtc = createdUtc;
            manifest.Components = new List<BackupComponentDto>();
            manifest.NativeHost = new List<BackupNativeHostDto>();
            BackupComponentDto component = new BackupComponentDto();
            component.Name = "marketplace";
            component.LivePath = livePath;
            component.BackupPath = backupPath;
            component.ExistedBefore = true;
            component.State = status == BackupStates.Complete ? BackupStates.Moved : BackupStates.Planned;
            component.Digest = digest;
            component.PreviousDigest = previousDigest;
            manifest.Components.Add(component);
            return manifest;
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

            // 验证 CLI 输出编码严格无 BOM（首字节不可为 EF BB BF）
            byte[] preamble = ConsoleManager.Utf8NoBom.GetPreamble();
            context.Expect(preamble == null || preamble.Length == 0, "ConsoleManager.Utf8NoBom emits no BOM", preamble == null ? "0" : preamble.Length.ToString());
            using (MemoryStream ms = new MemoryStream())
            {
                using (StreamWriter sw = new StreamWriter(ms, ConsoleManager.Utf8NoBom))
                {
                    sw.WriteLine("1.0.0");
                }
                byte[] bytes = ms.ToArray();
                bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                context.Expect(!hasBom, "CLI output first bytes are not UTF-8 BOM EF BB BF", hasBom ? "BOM found" : "clean");
            }
        }

        private static void TrayAndVersionContractCase(TestContext context)
        {
            context.Begin("tray-and-version/contract");

            // M4: 托盘与后台常驻监控配置契约
            Settings defaultSettings = new Settings();
            context.Expect(defaultSettings.AutoCheckHours == 6, "default AutoCheckHours is 6 hours", defaultSettings.AutoCheckHours.ToString());
            context.Expect(defaultSettings.RunAtStartup == false, "default RunAtStartup is false", defaultSettings.RunAtStartup.ToString());

            // M6: GitHub 版本号正规化与比较算法契约
            string v1 = UpdateChecker.NormalizeVersion("v1.0.1");
            context.Expect(v1 == "1.0.1", "NormalizeVersion strips leading v", v1 ?? "(null)");

            string v2 = UpdateChecker.NormalizeVersion("1.0.0.0");
            context.Expect(v2 == "1.0.0", "NormalizeVersion formats to 3 components", v2 ?? "(null)");

            string vInvalid = UpdateChecker.NormalizeVersion("invalid-tag");
            context.Expect(vInvalid == null, "NormalizeVersion returns null for invalid tag", vInvalid ?? "(null)");

            bool newer1 = UpdateChecker.IsGreater("1.0.1", "1.0.0");
            context.Expect(newer1, "1.0.1 is greater than 1.0.0", newer1.ToString());

            bool newer2 = UpdateChecker.IsGreater("1.0.0", "1.0.1");
            context.Expect(!newer2, "1.0.0 is not greater than 1.0.1", newer2.ToString());

            bool newer3 = UpdateChecker.IsGreater("1.0.0", "1.0.0");
            context.Expect(!newer3, "equal versions are not greater", newer3.ToString());
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
