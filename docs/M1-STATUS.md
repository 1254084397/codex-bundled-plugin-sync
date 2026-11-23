# M1 实施状态（增量 1：只读 Check）

> 对应文档：`docs/DESIGN.md` v0.3
> 本文件只记录"已经跑过、可复现"的事实，未完成的项单独列出，不写成"基本完成"。

## 已实现并可验证

| 范围 | 文件 | 验证方式 |
| --- | --- | --- |
| 构建（内置 MSBuild，单 exe 断言） | `build/CodexPluginSync.csproj` | `MSBuild -p:Configuration=Release` → 输出目录仅 `CodexPluginSync.exe` |
| 入口与参数分派 | `Program.cs`、`Cli/CliOptions.cs` | `--help` / `--version` / 未知参数退出 1（自测用例） |
| 退出码契约 C-4 | `Cli/ExitCodes.cs` | 0/1/2 三值，无第四值 |
| `--json` 信封 C-5 | `Cli/CliRunner.cs`、`Core/SnapshotWriter.cs` | 自测断言：stdout 只有一个 JSON 对象、`schemaVersion=1`、`exitCode` 与进程退出码同源 |
| 只读检查 §7.1 | `Core/CheckEngine.cs` | 自测断言：`CodexHome` 前后指纹一致、不建目录、不写 journal |
| `.materialization-key ∩ Desktop` | `Core/PluginInventory.cs` | 真机：9 个 Desktop 插件中只有 7 个进入 expected（`deep-research`、`plan` 被正确排除） |
| 缓存组件状态 | 同上 + `CheckEngine` | 自测：outdated / missing 两个分支 |
| Chrome native host 只读探测 | `Core/NativeHostProbe.cs`、`Core/PathIdentity.cs` | 真机 + 自测：junction 别名判定为 `ok` |
| 进程/磁盘/备份/journal/EFS 属性 | `ProcessGuard`、`BackupStore`、`OperationJournal`、`EfsProbe` | 真机快照 + 自测 journal=blocked 分支 |
| 日志根按操作类型 §6.5 | `Cli/CliRunner.cs`、`Core/Logger.cs` | `--check` 日志落 `%LOCALAPPDATA%\CodexPluginSync\logs`，CodexHome 零写入 |
| fixture 自测 §16.1 | `Core/Fixture.cs`、`Core/SelfTest.cs` | `--selftest` → 17 cases 全绿（含 `recovery/matrix` 20 条、`transaction/state-io` 12 条、`sync/transaction` 11 条断言） |
| **§6.6 恢复矩阵（C# 侧首次）** | `Core/RecoveryMatrix.cs` | 纯逻辑：sync 11 行、rollback 5 行、revert 行 + 终态一致性；`--selftest` 的 `recovery/matrix` 用例逐行断言 |
| **§6.3/§6.4 事务写侧（C#）** | `Core/TransactionState.cs` | `AtomicJsonFile`（临时文件 + `Flush(true)` + `File.Replace`、剥 BOM）、`JournalStore`（seq 写前递增、UTC、可删除）、`BackupManifestStore`（Write + `Validate`：components 为空不得 complete、state=moved 缺 `previousDigest` 直接拒）；`transaction/state-io` 覆盖 |
| **§9.4 native host 写入（C#）** | `Core/NativeHostWriter.cs` | 注册表默认值当外部数据：只接受用户目录内、文件名恰为 `com.openai.codexextension.json` 的路径；指向比较走 `PathIdentity`（junction 等价），漂移才写；写前备份原文件、写后重读校验；`nativehost/path-boundary` 5 条断言 |
| **§7.2 同步事务编排（C#）** | `Core/SyncTransaction.cs`、`Core/TreeDigest.cs` | Planned → BackupCreated → Staged → Verified → CommitStarted → Committed → Finalized 全阶段写 journal；CommitStarted 之后失败自动 Revert 并逐组件比摘要；stage 缺失时在动 live 之前中止。**纯编排**：不含 robocopy/EFS/native host（由调用方在 Staged 前完成），CLI 尚未接入 |
| **`--sync` 端到端（C#）** | `Core/SyncEngine.cs`、`Core/Installer.cs` | 解析 CodexHome/AppX → 组件集合（marketplace + browser/chrome/computer-use）→ stage（junction 不展开、复制后重建）→ 七阶段事务 → native host 钩子；`sync/engine` 用例（两次运行：完整同步 / 缓存命中跳过） |
| **§8 EFS（C#）** | `Core/Efs.cs`、`SyncEngine` | stage 前 `PrepareForWrite(marketParent)`，`finally` 统一 `Restore`；成功判据是**重新读到的 Encrypted 属性**（实测 `cipher /d` 对不存在路径仍返回 0），读不到即“未恢复”→ 退出码 2。`efs/state-machine` 6 条断言（卷不支持时记 skip，不宣称通过） |
| **§7.3 回滚（C#）** | `Core/RollbackEngine.cs` | 按 `createdUtc` 选最新 `status=complete`（或 `--backup <id>`）；非 complete 直接拒；动 live 前校验 `previousDigest`；安全快照写完并校验通过才动 live；先写满 `journal.targets` 再进 `rollback-commit-started`；失败退回安全快照、退不干净 → needs-attention 并保留 journal 与两份快照；遗留 journal 直接阻停（不猜）。`rollback/restore` 12 条 + `rollback/roundtrip` 8 条 + `rollback/guards` 11 条断言 |
| **§7.4 清理（C#）** | `Core/CleanupEngine.cs` | 身份一律读 `backup.json` 的 kind/status（legacy 才回退前缀）；`keepBackups` 只数 complete；`partial`/`verify-failed` 按天数；`kind=rollback-safety` 只留最新 1 个；空备份目录直删；stage 只删“超过 `staleStageHours` 且不被 journal 引用”；日志按 `keepLogDays`，最近 1 小时写过的不删；删除前做路径边界检查，reparse point 只删链接不递归。`--dry-run` 只出计划。`cleanup/retention` 18 条断言 |
| **§7.5 诊断导出 + 脱敏（C#）** | `Core/DiagExporter.cs`（含 `Redactor`） | 单 zip：`state.json`/`backups.json`/`settings.json`/`native-host.txt`/`environment.txt`/最近 5 个日志/`manifest.json`/`README.txt`；默认脱敏（用户名、计算机名、用户目录链、SID、临时目录），保留盘符；`--no-redact` 需显式传参且在 `manifest.json` 如实标记；不收集 token/密钥/cookie/插件文件/AppX/对话数据。`diag/redaction` 8 条断言 |
| **§6.6 崩溃恢复执行（C#）** | `Core/RecoveryRunner.cs`（+ `RecoveryMatrix`） | `--sync` / `--rollback` 开头先跑一次：判定只看 live/backup/stage(safety) 的存在性 + 树摘要（不依赖 journal 的 `swapped` 位）；执行前先做终态一致性预检，不一致时一个动作都不执行；每组件只允许矩阵那一行的唯一动作（`none`/`delete-stage`/`complete-swap`/`restore-backup`/`restore-safety`/`revert-new`/`delete-live`/`attention`）；全部落地才删 journal，否则写 `needs-attention` 并保留现场。journal 目标新增 `backupPath`/`existingBefore`/`oldDigest`/`newDigest`（恢复只读 journal）。`recovery/execute` 13 条断言（行 4/6/9 + 终态不一致） |

## 实测结果（本机）

```text
--selftest                      → SELFTEST PASS (26 cases)   ← 2026-11-24 构建后实测
--check（真机）                  → Verdict: ok, Update needed: No, exit 0
--check --json（stderr 丢弃）     → 单行 JSON 可解析，exitCode=2 时与进程退出码一致
--cleanup-only --dry-run（真机）  → 清理计划 114 项，exit 0，未删除任何内容
--export-diag --out <temp>.zip   → 12 个条目，manifest.json 标 redacted=true，zip 已删除
--bogus                         → exit 1
真机 native-messaging 清单哈希      → 2980548C…F9022（selftest 用 SkipNativeHost，未变）
```

真机 dry-run 暴露的一个保守选择：脚本层产生的 `backups\rollback-before-*` 目录（没有 `backup.json`）
会被归入“不是本工具创建的目录”而不动。清理器只按 `backup.json` 的 kind/status 认定身份，
例外只给 `openai-bundled-sync-*` 这个 legacy 前缀，宁可不删。

### 真机写入演练（2026-11-24）：撞到的三个 bug 与一个环境前提

| # | 现象 | 根因 | 修法 |
| --- | --- | --- | --- |
| 1 | `--sync` 第一步就 `同步失败：无法加密指定的文件` | AppX 源包里的文件本身带 EFS `Encrypted` 属性；`File.Copy` 把它拷到**未加密**目录会报 `ERROR_ENCRYPTION_FAILED`（6000），拷到加密目录反而正常 | `Installer.CopyFileRobust`：源加密 + 目标未加密时走字节流复制（时间戳手工补回），另留 6000 异常回退；用例 `efs/encrypted-source-copy` |
| 2 | 交换到 `cache:chrome` 时 `对路径 ...chrome 的访问被拒绝` | `extension-host.exe` 从 `cache\chrome\latest\...` 运行，锁住了待 rename 的目录；而**写入路径根本没调用进程守卫** | `SyncEngine` / `RollbackEngine` 写入前调用 `ProcessGuard.Detect`，命中就退 1 且不写任何东西；用例 `process/guard` |
| 3 | 交换中途失败后，未动过的组件被报成“退回不完整 → needs-attention” | `Revert` 只按 live 存在 + 备份缺失判定，未区分“本次压根没交换” | 未交换（`swappedOut/swappedIn` 均 false）的组件按“保持原样”处理；退干净时一并清掉 stage |

真机已因此验证到的：失败事务自动 Revert 后 `marketplace` 403 / `browser` 157 回到同步前内容；
遗留 journal 由 §6.6 恢复器按 row 2 收尾（清 stage、删 journal）。

环境前提（写进文档，不算 bug）：`extension-host` 由 Codex app-server daemon 监督重生，
停掉一个会再起一个；真正腾出 `cache\chrome` 需要先停 daemon（本次已征得用户同意后停用），
所以 **`--sync` 的真机成功路径仍未验收**，不能用 fixture 全绿代替。

真机发现并已修复的假阳性：Chrome manifest 指向 `...\chrome\latest\...`，`latest` 是指向版本目录的
junction。字符串比较会误报 `outdated`（且会去改写一个正确的文件）。现改为 `GetFinalPathNameByHandle`
解析后再比较，并补了 junction 回归用例。

## 脚本层（M1 增量 2 前置，与 C# 实现并行）

`scripts/*.ps1` 的旧实现按组内审查修了五处风险（回滚安全性、提交失败恢复、EFS 还原、
注册表路径边界、并发互斥），并补了 15 个 fixture 用例。
落点与验证方式见 `docs/REVIEW-FIXES.md`，测试入口见 `tests/README.md`。
C# 侧已接管全部写入命令 `--sync`、`--rollback`、`--cleanup-only`、`--export-diag`
（`Core/SyncEngine.cs`、`Core/RollbackEngine.cs`、`Core/CleanupEngine.cs`、`Core/DiagExporter.cs`）。
上表“已实现并可验证”为 C# 实测结果；脚本层保留为对照实现与验收入口。

## 未实现（不得视为已完成）

| 缺口 | 归属 | 依据 |
| --- | --- | --- |
| 崩溃后自动执行恢复矩阵的**真机强杀**演练 | ✅ 2026-11-24 已验收（commit-started 强杀 → 恢复 → 继续同步） | §6.6 |
| `--sync` / `--rollback` / `--cleanup-only` 的真机**成功路径** | ✅ 2026-11-24 已验收（见下方“真机写入演练”） | §7.2、§7.3、§7.4 |
| §6.6 行 4/8/11 的真机验证（毫秒级窗口，目前仅 fixture 覆盖） | M1 增量 5 | §6.6 |
| native host 回滚的“注册表值原本不存在 → 删键”分支（sync 不会建键，故未实现；脚本层已覆盖） | 同上 | §9.4 |
| WinForms 界面、托盘 | M3 / M4 | §11、§12 |
| `.cmd` 兼容映射（2→0） | M2 | §2.4 |

## 已知环境限制

- 本机无 .NET Framework 4.8 targeting pack，内置 MSBuild 回退到 4.0 程序集（`MSB3644` 警告）。
  csproj 已把 `mscorlib` 标为 `Private=false`，并用 `AssertSingleFileOutput` 硬断言输出目录只有 1 个 exe。
- 因此当前 exe 的 IL 级别等同 4.0 编译结果，仍需在装有 4.8 targeting pack 的环境重建后再做发布签核。
