# Codex Bundled Plugin Sync

> Windows utility for keeping Codex Desktop bundled plugins synchronized with a persistent local marketplace and cache.

[![Platform](https://img.shields.io/badge/platform-Windows-0078D4?logo=windows&logoColor=white)](https://github.com/1254084397/codex-bundled-plugin-sync)
[![PowerShell](https://img.shields.io/badge/PowerShell-5.1%2B-5391FE?logo=powershell&logoColor=white)](https://learn.microsoft.com/powershell/)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

[English](#english) · [中文](#中文)

**Current release: `1.0.0` · M3 GUI available · M4 tray and scheduled checks pending**

## English

Codex Desktop stores its bundled plugins inside a Windows AppX installation. A local Codex environment can also use a persistent marketplace and selected plugin caches under `CodexHome`. After a Desktop update, these locations can drift apart.

This utility provides a safe, repeatable way to:

- inspect whether synchronization is needed;
- copy the persistent bundled-plugin marketplace;
- update the managed browser, Chrome, and computer-use caches;
- verify copied files with SHA-256 hashes;
- update the Chrome native-messaging host path;
- keep a rollback snapshot;
- remove stale stages and old logs.

### Why this exists

Codex Desktop ships its bundled plugins inside a Windows AppX package, and loads them from a *second* copy materialized into `CodexHome`. A Store update replaces the AppX side and drops the old install path, while the `CodexHome` copy stays where it was. Nothing reports the mismatch — the plugins simply stop working.

This utility keeps both sides in step: it compares the Desktop package, the local marketplace, and the plugin caches, then copies, verifies, backs up, and (if needed) rolls back.

**Who it is for**

- Codex Desktop users who rely on the bundled browser, Chrome, or computer-use plugins.
- Anyone whose `CodexHome` was moved, customized, or edited by hand.
- People who need to script the same operation across machines.

**Is it necessary?**

Yes, if you have ever lost the browser plugin after a Desktop update and went digging through `WindowsApps` by hand. Not really, if you never use the bundled plugins — nothing breaks for you. This tool has a limited lifespan by design: if a future Codex Desktop release re-materializes plugins on its own, `--check` will permanently report `Update needed: No` and the project can be archived. Run `--check` only, for a while, before trusting it with writes.

### Why the check is reliable

The AppX bundle can contain plugins that are not part of the persistent Marketplace materialization. The scripts therefore compare the local Marketplace against Codex's materialization key instead of treating every directory in the AppX bundle as persistent. This avoids the common repeated-update false positive caused by transient or app-internal plugins.

### Quick start

Double-click `CodexPluginSync.exe` — that opens the Windows desktop interface. It shows the current tool version and checks GitHub Releases for a newer published version in the background. Press **检查** to see the current Codex state (nothing is written), then **同步** if it says an update is needed. Both write actions ask for confirmation first.

![主界面](docs/screenshots/gui-main.png)

Prefer the command line? Same exe, same behaviour:

1. Close Codex Desktop, ChatGPT, Chrome, and Edge (and any browser that loads the Codex extension — see *What blocks a sync* below).
2. Run the read-only check:

   ```bat
   CodexPluginSync.exe --check
   ```

3. If the check reports `Update needed: Yes`, run:

   ```bat
   CodexPluginSync.exe --sync
   ```

4. Restart Codex Desktop after a successful synchronization.

Both entry points resolve `CodexHome` the same way. To use another location:

```bat
set CODEX_HOME=E:\CodexHome
CodexPluginSync.exe --check
```

> The legacy `sync-codex-bundled.cmd` wrapper still works and defaults to “sync” with no arguments. When `CodexPluginSync.exe` is present (such as in release zips), it calls the exe directly and maps exit code `2` back to `0` for batch compatibility. Note the automation boundary: with no arguments, `.cmd` pauses on exit so double-clicking in Explorer keeps the window open. If invoking `.cmd` with no arguments from Windows Task Scheduler or automated scripts, pass `--no-pause` or set `CODEX_NO_PAUSE=1` (or `CI=1`); explicit command arguments like `--check` or `--sync` do not pause. In raw source checkouts without the binary, it falls back to the PowerShell scripts under `scripts/`.

### What blocks a sync

The sync replaces whole directories, so anything holding a file inside them must be closed first. In practice that is not only Chrome/Edge: the Codex browser extension's `extension-host.exe` is started by whichever Chromium browser loads the extension, and some Codex helper processes restart it automatically. If a sync is refused with `检测到会锁住插件目录的进程`, close the listed program (or add its process name to `extraLockingProcesses` in `%LOCALAPPDATA%\CodexPluginSync\settings.json`) and retry. The tool refuses before writing anything, and a failed run reverts automatically.

### Commands

| Command | Purpose | Writes plugin files? |
| --- | --- | :---: |
| *(no arguments)* | Open the GUI | No |
| `--check` | Show Desktop, Marketplace, cache, disk, and process status | No |
| `--sync` | Synchronize Marketplace and managed caches | Yes |
| `--rollback [--backup <id>]` | Restore a completed synchronization backup (newest by default) | Yes |
| `--cleanup-only [--dry-run]` | Remove old backups, stages, and logs | No plugin changes |
| `--export-diag [--out <path>] [--no-redact]` | Write a redacted diagnostic zip | No |
| `--selftest` | Run the built-in fixture suite | No (temp dirs only) |
| `--help` | Print command help | No |

The PowerShell scripts can also be called directly and accept `-Root`:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\check-openai-bundled.ps1 -Root "$env:USERPROFILE\CodexHome"
powershell -ExecutionPolicy Bypass -File .\scripts\sync-openai-bundled.ps1 -Root "$env:USERPROFILE\CodexHome"
```

### What is protected

- The check mode is non-destructive.
- Source files in the AppX installation are read only.
- A backup is created before the Marketplace or managed cache is replaced.
- Copied files are compared with SHA-256 hashes.
- Logs are written under `CodexHome\logs`.
- No credentials, API keys, tokens, or personal logs are included in this repository.

### Requirements

- Windows 10 or Windows 11
- Codex Desktop installed as a Microsoft Store/AppX package
- .NET Framework 4.8 runtime (built-in on Windows 10 1903+ and Windows 11)
- Permission to read the AppX installation and write to the selected `CodexHome`
- *For building from source:* in-box MSBuild or Visual Studio. Note that without the .NET 4.8 Targeting Pack, local builds compile against in-box runtime assemblies; official GitHub Release artifacts should be built in an environment with the .NET 4.8 Developer Pack or CI runner.

### Current status and roadmap

The project now ships a **single-exe distribution** with a WinForms status screen, sync/rollback/cleanup/diagnostic actions, background work, release-version display, and the headless CLI. The specification and remaining boundaries are recorded in **[docs/DESIGN.md](docs/DESIGN.md)**. The test and real-machine evidence is summarized in **[docs/M1-STATUS.md](docs/M1-STATUS.md)**.

| Stage | Scope | Done when |
| --- | --- | :--- |
| M0 | Freeze contracts: state machine, backup model, exit codes, JSON schema, recovery, EFS/native-host/redaction boundaries | Done |
| M1 | Headless core + selftest: paths, check, sync, rollback, cleanup, journal, backup store, hasher | Done; selftest and real-machine evidence recorded |
| M2 | CLI compatibility layer: arguments, exit codes, `--json`, `.cmd` calls the exe | Done for the current release |
| M3 | WinForms UI: status screen, sync/rollback/cleanup/diagnostics buttons, log pane, release update status | Done in `1.0.0` |
| M4 | Tray icon and scheduled checks (off by default) | Planned |
| M5 | Broader CI/release automation and final acceptance sweep | Planned |

Build a release zip for someone else:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\package.ps1
```

That builds, refuses to package unless `--selftest` is green, and writes `dist\CodexPluginSync-<version>.zip` (exe + sync-codex-bundled.cmd + README + CHANGELOG + LICENSE). The current release is `1.0.0`; the GUI also reports when GitHub has a newer release.

Design notes worth knowing now:

- Target: C# WinForms on .NET Framework 4.8, **single-exe distribution** — one `CodexPluginSync.exe`, no third-party DLLs, no installers. This is not a self-contained build: the machine must already have .NET Framework 4.8 (in-box on Windows 10 1903+ and Windows 11). `robocopy.exe` and `cipher.exe` are OS tools, not shipped dependencies.
- The PowerShell logic moves *into* the exe instead of being shelled out to, so a single downloaded file is enough to run.
- The existing scripts stay as a legacy entry point for scheduled tasks and scripts; the `.cmd` wrapper keeps working, and its no-argument default still means "sync".
- Backups become component snapshots: a run that only updated one plugin cache records and restores only that component, instead of demanding every cache be present.
- The EFS sequence (`cipher /e` on the staging parent, `robocopy /COPY:DAT`, then `cipher /d`, with an encrypted-attribute recheck) is treated as a contract, not a detail. Do not replace it with a plain file copy, and do not assume a fixture test proves it works.
- `--check` will exit `2` when an update is needed; the current script exits `0`. The `.cmd` wrapper maps `2` back to `0` so existing automation is unaffected.

### Repository layout

```text
.
├── src/CodexPluginSync/
│   ├── Program.cs            入口：无参数→GUI，带参数→CLI
│   ├── Cli/                  参数、退出码、报告输出
│   ├── Core/                 检查、同步事务、回滚、清理、恢复矩阵、EFS、日志
│   └── Gui/                  WinForms 主界面
├── build/
│   ├── CodexPluginSync.csproj
│   └── package.ps1           构建 + 自检 + 打 zip
├── scripts/                  legacy PowerShell 实现（对照与计划任务用）
├── docs/
│   ├── DESIGN.md             契约
│   ├── M1-STATUS.md          已实现/未实现（实测口径）
│   ├── REVIEW-FIXES.md       审查与真机事故记录
│   └── screenshots/
├── tests/                    PowerShell fixture 与 EFS 验收
├── sync-codex-bundled.cmd
├── LICENSE
└── README.md
```

### Disclaimer

This is an unofficial community utility. It is not affiliated with or endorsed by OpenAI. Codex Desktop packaging, AppX paths, and plugin behavior may change. Run `--check` after each Desktop update and inspect the generated log if synchronization fails.

## 中文

这是一个用于 Windows 的 Codex Desktop 内置插件同步工具。它将 Codex Desktop AppX 安装包中的内置插件，同步到本地持久化 Marketplace 和插件缓存目录，并提供校验、备份、回滚、清理和状态检查功能。

### 它解决什么问题

Codex Desktop 更新后，AppX 安装目录、本地 Marketplace 和插件缓存可能出现版本不一致。此工具可以检查是否需要同步，并在复制完成后执行 SHA-256 完整性校验。

工具会区分：

- AppX 安装包中的完整插件目录；
- Codex 实际持久化的 Marketplace 插件清单。

因此，安装包中存在但不属于持久化 Marketplace 的插件不会导致每次重复提示更新。

### 为什么做这个软件

Codex Desktop 的内置插件装在 AppX 包里，运行时却从 `CodexHome` 中另复制出来的一份加载。Store 更新只替换 AppX 那一侧，旧版本目录直接消失，而 `CodexHome` 里的拷贝原地不动。这个不一致不会有任何提示，表现出来的只是「插件突然不能用了」。

这个工具把两侧对齐：比对 Desktop 安装包、本地 Marketplace 与插件缓存，然后复制、校验、备份，必要时回滚。

**给谁用**

- 依赖内置浏览器 / Chrome / 电脑操作插件的 Codex Desktop 用户；
- `CodexHome` 被迁移、改过路径或手工动过的用户；
- 需要在多台机器上把同一套操作脚本化的用户。

**有没有必要**

如果你曾经在 Desktop 更新后发现浏览器插件失灵、只能自己去翻 `WindowsApps` 目录，那就有必要。如果你从不用内置插件，那同步不做也没有可感知影响，用不上。

需要说明的是，这个工具有明确的寿命：如果将来某个版本的 Codex Desktop 自己重新物化插件，`--check` 会长期返回 `Update needed: No`，项目就该归档。所以建议先只用 `--check` 观察一两个版本，确认状态确实会随更新漂移，再让它执行写入操作。

### 当前状态与路线图

现在已经提供**单 exe 分发 + WinForms 图形界面**：状态检查、同步、回滚、清理、诊断导出、后台执行，以及 GitHub 发布版本检查。设计契约见 **[docs/DESIGN.md](docs/DESIGN.md)**，实现和真机证据见 **[docs/M1-STATUS.md](docs/M1-STATUS.md)**。

| 阶段 | 内容 | 完成判据 |
| --- | --- | :--- |
| M0 | 冻结契约：状态机、备份模型、退出码、JSON schema、恢复策略、EFS/注册表/脱敏边界 | 已完成 |
| M1 | 无界面 Core + selftest：路径、检查、同步、回滚、清理、journal、备份存储、哈希 | 已完成；已有 fixture 和真机证据 |
| M2 | CLI 兼容层：参数、退出码、`--json`、`.cmd` 调用 exe | 当前版本已完成 |
| M3 | WinForms 主界面：状态、同步/回滚/清理/诊断按钮、日志区、版本更新状态 | `1.0.0` 已完成 |
| M4 | 托盘与定时检查（默认关闭） | 计划中 |
| M5 | 更完整的 CI/发布自动化与最终验收 | 计划中 |

给别人用的发布包：

```powershell
powershell -ExecutionPolicy Bypass -File .\build\package.ps1
```

它会先构建、自检不过就拒绝打包，最后生成 `dist\CodexPluginSync-<版本>.zip`（exe + sync-codex-bundled.cmd + README + CHANGELOG + LICENSE）。

现在就该知道的几个设计点：

- 技术选型是 C# WinForms + .NET Framework 4.8，交付形态是**单 exe 分发**：一个 `CodexPluginSync.exe`，无第三方 DLL、无安装器。注意这**不是自包含构建**：目标机必须已有 .NET Framework 4.8（Win10 1903+ 与 Win11 自带）。`robocopy.exe`、`cipher.exe` 属于系统工具，不算随程序分发的依赖。
- 逻辑会搬进 exe，而不是让 exe 去调用 ps1 —— 这样只下载一个文件就能用。
- 现有脚本保留为 legacy 入口，计划任务不受影响；`.cmd` 无参数仍然是“同步”。
- 备份改成组件快照：只更新了一个插件缓存的那次运行，回滚时也只动那一项，不再要求四个组件全部存在。
- EFS 那套序列（stage 父目录 `cipher /e` → `robocopy /COPY:DAT` → `cipher /d` → 复查加密属性）是契约而不是细节。**不要换成普通文件复制**，也不要以为 fixture 测试能证明它在真机上成立。
- `--check` 在需要更新时会以退出码 `2` 结束，而当前脚本返回 `0`。`.cmd` 会把 `2` 映射回 `0`，现有自动化不受影响。

### 快速使用

双击 `CodexPluginSync.exe` 就是界面：窗口会显示当前工具版本，并在后台检查 GitHub 是否有更新；先点 **检查**（只读，不写任何东西），需要更新时再点 **同步**。两个写操作都会先弹确认框。

![主界面](docs/screenshots/gui-main.png)

也可以用命令行（同一个 exe、同一套行为）：

先关闭 Codex Desktop、ChatGPT、Chrome 和 Edge（以及任何加载了 Codex 扩展的浏览器，见下方“什么会阻止同步”），然后：

```bat
CodexPluginSync.exe --check
```

如果输出：

```text
Update needed: Yes
```

再运行：

```bat
CodexPluginSync.exe --sync
```

同步成功后重新启动 Codex Desktop。

默认目录是 `%USERPROFILE%\CodexHome`，也可以指定：

```bat
set CODEX_HOME=E:\CodexHome
CodexPluginSync.exe --check
```

> 旧的 `sync-codex-bundled.cmd` 仍然可用，无参数依旧默认执行“同步”。当目录中存在 `CodexPluginSync.exe`（如发布包解压后）时，它会自动调用 exe 并将退出码 `2` 映射回 `0`，无需依赖 `scripts/`。**注意自动化边界**：无参数启动时 `.cmd` 会在末尾执行 `pause`（方便在文件管理器中双击查看输出）；若在 Windows 计划任务或后台脚本中**无参数**调用，需显式传入 `--no-pause` 或设置环境变量 `CODEX_NO_PAUSE=1`（或 `CI=1`）避免挂起；显式传入 `--check` / `--sync` / `--rollback` / `--cleanup-only` 等参数时则默认不会暂停。在源码环境中若未编译 exe，则自动回落到 `scripts/` 的 PowerShell 实现。

### 什么会阻止同步

同步是整目录替换，所以只要有人拿着这些目录里的文件就必须先关掉。实践中不只是 Chrome/Edge：Codex 浏览器扩展的 `extension-host.exe` 由**加载该扩展的任意 Chromium 浏览器**拉起，某些 Codex 辅助进程还会把它重启。

如果同步被拒绝并提示 `检测到会锁住插件目录的进程`，把列出的程序关掉（或把进程名加进 `%LOCALAPPDATA%\CodexPluginSync\settings.json` 的 `extraLockingProcesses`）再试。工具会在**写入之前**拒绝，失败运行会自动回退。

### 安全设计

- `--check` 模式不修改插件文件；
- 替换 Marketplace 或缓存前创建备份；
- 复制完成后执行 SHA-256 校验；
- 支持回滚最近一次同步；
- 日志保存在 `CodexHome\logs`；
- 仓库不包含凭据、API Key、Token 或个人日志；
- 原始 AppX 文件只读取，不覆盖。

## License

MIT License. See [LICENSE](LICENSE).

