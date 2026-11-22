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

### Repository layout

```text
.
├── sync-codex-bundled.cmd
├── scripts/
│   ├── check-openai-bundled.ps1
│   ├── cleanup-openai-bundled.ps1
│   ├── rollback-openai-bundled.ps1
│   └── sync-openai-bundled.ps1
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

