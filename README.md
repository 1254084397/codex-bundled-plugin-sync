# Codex Bundled Plugin Sync

> Windows utility for keeping Codex Desktop bundled plugins synchronized with a persistent local marketplace and cache.

[![Platform](https://img.shields.io/badge/platform-Windows-0078D4?logo=windows&logoColor=white)](https://github.com/1254084397/codex-bundled-plugin-sync)
[![PowerShell](https://img.shields.io/badge/PowerShell-5.1%2B-5391FE?logo=powershell&logoColor=white)](https://learn.microsoft.com/powershell/)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

[English](#english) · [中文](#中文)

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

1. Close Codex Desktop, ChatGPT, Chrome, and Edge.
2. Download or clone this repository.
3. Run the read-only check:

   ```bat
   sync-codex-bundled.cmd --check
   ```

4. If the check reports `Update needed: Yes`, run:

   ```bat
   sync-codex-bundled.cmd --sync
   ```

5. Restart Codex Desktop after a successful synchronization.

The wrapper defaults to `%USERPROFILE%\CodexHome`. To use another location:

```bat
set CODEX_HOME=E:\CodexHome
sync-codex-bundled.cmd --check
```

### Commands

| Command | Purpose | Writes plugin files? |
| --- | --- | :---: |
| `--check` | Show Desktop, Marketplace, cache, disk, and process status | No |
| `--sync` | Synchronize Marketplace and managed caches | Yes |
| `--cleanup-only` | Remove old backups, stages, and logs | No plugin changes |
| `--rollback` | Restore the newest completed synchronization backup | Yes |
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
- PowerShell 5.1 or later
- Permission to read the AppX installation and write to the selected `CodexHome`

### Roadmap

The next milestone turns this into a single signed-free `.exe` with a GUI: one status screen, one sync button, real progress, backup browsing, a tray indicator, and a diagnostic export. The full specification — architecture, data formats, transaction and crash-recovery rules, UI layout, failure matrix, risks — lives in **[docs/DESIGN.md](docs/DESIGN.md)**.

| Stage | Scope | Result |
| --- | --- | :--- |
| M1 | Core skeleton, read-only status screen, `--check --json` | GUI replacement for `--check` |
| M2 | Copy/verify/swap transaction, progress, cancel, process guard | GUI replacement for `--sync`, safer than the scripts |
| M3 | Rollback and cleanup engines, backup browser, restore any backup | Replaces `--rollback` and `--cleanup-only` |
| M4 | Settings, tray, scheduled checks, diagnostic export | Feature-complete |
| M5 | Build script, CI, README rewrite, `--selftest` fixture | v1.0 |

Design notes worth knowing now:

- Target: C# WinForms on .NET Framework 4.8 — Win10/11 has the runtime in-box, so the exe stays around 200–300 KB with zero dependencies.
- The PowerShell logic moves *into* the exe instead of being shelled out to, so a single downloaded file is enough to run.
- The existing scripts stay as a legacy entry point for scheduled tasks and scripts; the `.cmd` wrapper keeps working.
- The EFS workaround (`cipher /e` on the staging parent, `robocopy /COPY:DAT`, then `cipher /d`) is carried over unchanged. Do not replace it with a plain file copy.

### Repository layout

```text
.
├── sync-codex-bundled.cmd
├── scripts/
│   ├── check-openai-bundled.ps1
│   ├── cleanup-openai-bundled.ps1
│   ├── rollback-openai-bundled.ps1
│   └── sync-openai-bundled.ps1
├── docs/
│   └── DESIGN.md
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

### Roadmap / 路线图

下一步会把它做成**单文件 `.exe` + 图形界面**：一个状态页、一个同步按钮、真实进度、备份浏览、托盘提示和诊断导出。完整规格（架构、数据格式、事务与崩溃恢复规则、界面布局、失败矩阵、风险清单）见 **[docs/DESIGN.md](docs/DESIGN.md)**。

| 阶段 | 内容 | 结果 |
| --- | --- | :--- |
| M1 | Core 骨架、只读状态页、`--check --json` | 图形版替代 `--check` |
| M2 | 复制/校验/交换事务、进度、取消、进程占用处理 | 图形版替代 `--sync`，比脚本更安全 |
| M3 | 回滚与清理引擎、备份浏览、恢复任意备份 | 取代 `--rollback` 与 `--cleanup-only` |
| M4 | 设置页、托盘、定时检查、诊断导出 | 功能完整 |
| M5 | 构建脚本、CI、README 重写、`--selftest` fixture | v1.0 |

现在就该知道的几个设计点：

- 技术选型是 .NET Framework 4.8 + WinForms：Win10/11 自带运行时，exe 约 200–300 KB，零依赖。
- 逻辑会搬进 exe，而不是让 exe 去调用 ps1 —— 这样只下载一个文件就能用。
- 现有脚本保留为 legacy 入口，计划任务和批处理不受影响，`.cmd` 继续可用。
- EFS 那套处理（stage 父目录 `cipher /e` → `robocopy /COPY:DAT` → `cipher /d`）原样保留，**不要换成普通文件复制**，否则在 E: 这类盘上会失败。

### 快速使用

先关闭 Codex Desktop、ChatGPT、Chrome 和 Edge，然后运行：

```bat
sync-codex-bundled.cmd --check
```

如果输出：

```text
Update needed: Yes
```

再运行：

```bat
sync-codex-bundled.cmd --sync
```

同步成功后重新启动 Codex Desktop。

默认目录是：

```text
%USERPROFILE%\CodexHome
```

也可以指定自定义目录：

```bat
set CODEX_HOME=E:\CodexHome
sync-codex-bundled.cmd --check
```

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

