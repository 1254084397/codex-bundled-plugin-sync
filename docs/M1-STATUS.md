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
| fixture 自测 §16.1 | `Core/Fixture.cs`、`Core/SelfTest.cs` | `--selftest` → 12 cases 全绿 |

## 实测结果（本机）

```text
--selftest                      → SELFTEST PASS (12 cases)
--check（真机）                  → Verdict: ok, Update needed: No, exit 0
--check --json（stderr 丢弃）     → 单行 JSON 可解析，exitCode=2 时与进程退出码一致
--bogus                         → exit 1
```

真机发现并已修复的假阳性：Chrome manifest 指向 `...\chrome\latest\...`，`latest` 是指向版本目录的
junction。字符串比较会误报 `outdated`（且会去改写一个正确的文件）。现改为 `GetFinalPathNameByHandle`
解析后再比较，并补了 junction 回归用例。

## 脚本层（M1 增量 2 前置，与 C# 实现并行）

`scripts/*.ps1` 的旧实现按组内审查修了五处风险（回滚安全性、提交失败恢复、EFS 还原、
注册表路径边界、并发互斥），并补了 15 个 fixture 用例。
落点与验证方式见 `docs/REVIEW-FIXES.md`，测试入口见 `tests/README.md`。
C# 侧尚未接管 `--sync` / `--rollback`，因此下表仍然有效。

## 未实现（不得视为已完成）

| 缺口 | 归属 | 依据 |
| --- | --- | --- |
| `--sync` / `--rollback` / `--cleanup-only` / `--export-diag` | M1 增量 2+ | §7.2、§7.3、§7.4、§7.5 |
| journal 写侧、`operationKind`、§6.6 恢复矩阵 11 行 | 同上 | §6.4、§6.6 |
| 备份写入与移入后校验、`status=complete` 合成判据 | 同上 | §6.3、§7.2 |
| 回滚状态机与安全快照 | 同上 | §7.3 |
| Native host 写入与 §9.4 存在性位 | 同上 | §9.4 |
| EFS 探测/开启/撤销（含探测残留处置） | 同上 | §8.1、§8.2 |
| `Redactor` / 诊断包 | 同上 | §7.5 |
| WinForms 界面、托盘 | M3 / M4 | §11、§12 |
| `.cmd` 兼容映射（2→0） | M2 | §2.4 |

## 已知环境限制

- 本机无 .NET Framework 4.8 targeting pack，内置 MSBuild 回退到 4.0 程序集（`MSB3644` 警告）。
  csproj 已把 `mscorlib` 标为 `Private=false`，并用 `AssertSingleFileOutput` 硬断言输出目录只有 1 个 exe。
- 因此当前 exe 的 IL 级别等同 4.0 编译结果，仍需在装有 4.8 targeting pack 的环境重建后再做发布签核。
