using System.Collections.Generic;

namespace CodexPluginSync.Core
{
    /// <summary>
    /// §6.6 恢复矩阵（纯逻辑，不做任何 IO）。
    ///
    /// 与 scripts/lib/CodexPluginSync.Common.ps1 的 Resolve-CodexSyncTargetRow / Resolve-CodexSyncRevertRow
    /// 保持同一套行语义；脚本层已在真机验收通过，C# 侧接管 sync/rollback 时以本类为准。
    /// 调用方负责把"存在性 + 树摘要"折算成这里的布尔量，并负责执行返回的动作。
    /// </summary>
    public static class RecoveryMatrix
    {
        public const string ActionNone = "none";
        public const string ActionDeleteStage = "delete-stage";
        public const string ActionCompleteSwap = "complete-swap";
        public const string ActionRestoreBackup = "restore-backup";
        public const string ActionRestoreSafety = "restore-safety";
        public const string ActionRevertNew = "revert-new";
        public const string ActionDeleteLive = "delete-live";
        public const string ActionAttention = "attention";

        public const string OutcomeAborted = "aborted";
        public const string OutcomeRecovered = "recovered";
        public const string OutcomeCommitted = "committed";
        public const string OutcomeReverted = "reverted";
        public const string OutcomeAttention = "attention";

        /// <summary>一个组件在恢复时的全部输入。摘要匹配结果由调用方折算成布尔量。</summary>
        public sealed class TargetState
        {
            public string Component;
            public bool ExistedBefore = true;
            public bool LiveExists;
            public bool LiveIsOld;
            public bool LiveIsNew;
            public bool BackupExists;
            public bool BackupIsOld;
            public bool StageExists;
            public bool StageIsNew;
            public bool SafetyExists;
            public bool SafetyIsOld;
        }

        public sealed class Decision
        {
            public int Row;
            public string Action;
            public string Outcome;
            public string Reason;

            public Decision(int row, string action, string outcome, string reason)
            {
                Row = row;
                Action = action;
                Outcome = outcome;
                Reason = reason;
            }
        }

        /// <summary>
        /// §6.6 的 11 行矩阵。stage 存在但内容不完整（StageIsNew=false）不属于任何一行：
        /// 直接落行 11，且要求调用方保留 stage 作为现场。
        /// </summary>
        public static Decision ResolveSync(TargetState state)
        {
            if (state == null)
            {
                return new Decision(11, ActionAttention, OutcomeAttention, "缺少组件状态");
            }
            if (state.StageExists && !state.StageIsNew)
            {
                return new Decision(11, ActionAttention, OutcomeAttention, "stage 内容不完整或无法识别，矩阵不覆盖该组合");
            }

            if (!state.LiveExists)
            {
                if (state.BackupIsOld)
                {
                    if (state.StageIsNew)
                    {
                        return new Decision(4, ActionCompleteSwap, OutcomeCommitted, "交换第 1 步完成、stage 完整，继续前滚");
                    }
                    return new Decision(8, ActionRestoreBackup, OutcomeRecovered, "live 缺失、stage 丢失，退回备份内容");
                }
                if (!state.BackupExists && !state.StageExists)
                {
                    return new Decision(9, ActionAttention, OutcomeAttention, "live/backup/stage 三份都不存在");
                }
                return new Decision(11, ActionAttention, OutcomeAttention, "备份存在但内容与 oldDigest 不一致");
            }

            if (state.LiveIsOld)
            {
                if (state.BackupIsOld)
                {
                    if (state.StageExists)
                    {
                        return new Decision(10, ActionDeleteStage, OutcomeAborted, "三份齐全且未交换");
                    }
                    return new Decision(3, ActionNone, OutcomeRecovered, "备份已生成但 live 未动");
                }
                if (!state.BackupExists)
                {
                    if (state.StageExists)
                    {
                        return new Decision(2, ActionDeleteStage, OutcomeAborted, "已 staged 未 commit");
                    }
                    return new Decision(1, ActionNone, OutcomeAborted, "尚未备份、未交换");
                }
                return new Decision(11, ActionAttention, OutcomeAttention, "备份内容与 oldDigest 不一致");
            }

            if (state.LiveIsNew)
            {
                if (state.BackupIsOld)
                {
                    if (state.StageExists)
                    {
                        return new Decision(6, ActionDeleteStage, OutcomeCommitted, "交换完成但 stage 未清");
                    }
                    return new Decision(5, ActionNone, OutcomeCommitted, "交换完成");
                }
                if (!state.BackupExists)
                {
                    if (!state.ExistedBefore)
                    {
                        return new Decision(7, ActionNone, OutcomeCommitted, "existedBefore=false 的新建组件");
                    }
                    return new Decision(11, ActionAttention, OutcomeAttention, "existedBefore=true 但备份不存在");
                }
                return new Decision(11, ActionAttention, OutcomeAttention, "备份内容与 oldDigest 不一致");
            }

            return new Decision(11, ActionAttention, OutcomeAttention, "live 摘要既不匹配 oldDigest 也不匹配 newDigest");
        }

        /// <summary>回滚（operationKind=rollback）：oldDigest 是回滚前 live，newDigest 是要回填的备份内容。</summary>
        public static Decision ResolveRollback(TargetState state)
        {
            if (state == null)
            {
                return new Decision(11, ActionAttention, OutcomeAttention, "缺少组件状态");
            }
            if (state.StageExists && !state.StageIsNew)
            {
                return new Decision(11, ActionAttention, OutcomeAttention, "stage 内容不完整或无法识别，矩阵不覆盖该组合");
            }

            if (!state.LiveExists)
            {
                if (state.SafetyIsOld)
                {
                    return new Decision(3, ActionRestoreSafety, OutcomeRecovered, "live 缺失，安全快照内容完整");
                }
                if (!state.SafetyExists)
                {
                    return new Decision(8, ActionAttention, OutcomeAttention, "live 与安全快照都不存在");
                }
                return new Decision(10, ActionAttention, OutcomeAttention, "安全快照内容与 oldDigest 不一致");
            }

            if (state.LiveIsOld)
            {
                if (state.SafetyExists && !state.SafetyIsOld)
                {
                    return new Decision(10, ActionAttention, OutcomeAttention, "live 未动但安全快照内容不可识别");
                }
                if (state.StageExists)
                {
                    return new Decision(1, ActionDeleteStage, OutcomeAborted, "live 仍是回滚前内容，只清理 stage");
                }
                return new Decision(2, ActionNone, OutcomeAborted, "live 未动、无 stage");
            }

            if (state.LiveIsNew)
            {
                if (state.SafetyIsOld)
                {
                    if (state.StageExists)
                    {
                        return new Decision(6, ActionRestoreSafety, OutcomeRecovered, "回滚已生效，退回安全快照并清理 stage");
                    }
                    return new Decision(5, ActionRestoreSafety, OutcomeRecovered, "回滚已生效，退回安全快照");
                }
                return new Decision(7, ActionAttention, OutcomeAttention, "回滚已生效但安全快照不可用，无法退回");
            }

            return new Decision(11, ActionAttention, OutcomeAttention, "live 摘要既不匹配同步前内容也不匹配回滚内容");
        }

        /// <summary>
        /// 撤回目标（进程内失败处理）：正常结果统一标记为 reverted，
        /// 因此"逐组件差异"不会被误判成混合终态。
        /// </summary>
        public static Decision ResolveRevert(TargetState state, bool rollbackKind)
        {
            if (state == null)
            {
                return new Decision(11, ActionAttention, OutcomeAttention, "缺少组件状态");
            }
            if (state.LiveIsOld)
            {
                return new Decision(1, ActionNone, OutcomeReverted, "live 已是本次操作前内容");
            }

            if (state.LiveIsNew || !state.LiveExists)
            {
                if (rollbackKind)
                {
                    if (state.SafetyIsOld)
                    {
                        return new Decision(5, ActionRestoreSafety, OutcomeReverted, "退回安全快照");
                    }
                    if (state.SafetyExists)
                    {
                        return new Decision(10, ActionAttention, OutcomeAttention, "安全快照内容与 oldDigest 不一致");
                    }
                    return new Decision(7, ActionAttention, OutcomeAttention, "安全快照不存在");
                }

                if (state.BackupIsOld)
                {
                    return new Decision(5, ActionRevertNew, OutcomeReverted, "退回备份内容");
                }
                if (state.BackupExists)
                {
                    return new Decision(11, ActionAttention, OutcomeAttention, "备份内容与 oldDigest 不一致");
                }
                if (!state.ExistedBefore)
                {
                    return new Decision(7, ActionDeleteLive, OutcomeReverted, "本次新建的组件，撤回即移出");
                }
                return new Decision(9, ActionAttention, OutcomeAttention, "备份不存在且组件本该存在");
            }

            return new Decision(11, ActionAttention, OutcomeAttention, "live 摘要无法识别");
        }

        /// <summary>
        /// 终态一致性：全部组件必须落在同一终态。返回 null 表示一致，
        /// 否则返回可直接写日志的原因（调用方应保留 journal 并报 attention，且不得先执行任何动作）。
        /// </summary>
        public static string FindInconsistentOutcome(List<Decision> decisions)
        {
            if (decisions == null || decisions.Count == 0)
            {
                return null;
            }
            string first = decisions[0].Outcome;
            for (int i = 1; i < decisions.Count; i++)
            {
                if (decisions[i].Outcome != first)
                {
                    return "组件终态不一致，不自动收尾：" + Describe(decisions);
                }
            }
            return null;
        }

        public static string Describe(List<Decision> decisions)
        {
            if (decisions == null)
            {
                return string.Empty;
            }
            List<string> parts = new List<string>();
            for (int i = 0; i < decisions.Count; i++)
            {
                parts.Add(decisions[i].Row.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + decisions[i].Outcome);
            }
            return string.Join(", ", parts.ToArray());
        }
    }
}
