# Codex bundled plugin sync

Windows scripts for synchronizing the bundled plugins shipped with Codex Desktop into a persistent local marketplace/cache, with SHA-256 verification, backups, rollback, cleanup, and a non-destructive status check.

## Why this exists

Codex Desktop keeps its bundled plugin payload inside the Windows AppX installation. A local Codex environment may also use a persistent marketplace and selected plugin caches under `CodexHome`. After a Desktop update, those locations can be out of sync. This project copies only the persistent marketplace set, verifies the copied files, updates the Chrome native-messaging host path, and keeps one rollback backup.

The scripts intentionally distinguish the complete Desktop bundle from the persistent marketplace manifest. This prevents transient or app-internal plugins from causing a false update on every run.

## Requirements

- Windows 10/11
- Codex Desktop installed from the Microsoft Store/AppX package
- PowerShell 5.1 or later
- Permission to read the Codex Desktop installation and write the selected `CodexHome`

Close Codex Desktop, ChatGPT, Chrome, and Edge before synchronization or rollback.

## Usage

Double-click `sync-codex-bundled.cmd`, or run:

```bat
sync-codex-bundled.cmd --check
sync-codex-bundled.cmd --sync
sync-codex-bundled.cmd --cleanup-only
sync-codex-bundled.cmd --rollback
```

By default the wrapper uses `%USERPROFILE%\CodexHome`. Set `CODEX_HOME` to use another location, for example:

```bat
set CODEX_HOME=E:\CodexHome
sync-codex-bundled.cmd --check
```

The PowerShell scripts also accept `-Root` directly. The synchronizer writes logs under `CodexHome\logs`, keeps a rollback backup under `CodexHome\backups`, and removes stale staging directories and old logs according to the wrapper settings.

## Safety behavior

- The check mode does not modify plugin files.
- The synchronizer compares the Desktop bundle with the persistent marketplace set, not every directory in the AppX bundle.
- Copied files are verified with SHA-256 hashes.
- A previous marketplace/cache state is backed up before replacement.
- Rollback and cleanup are separate commands.
- The scripts do not contain credentials or API keys.

This project is an unofficial community utility. It is not affiliated with or endorsed by OpenAI. AppX installation paths, plugin packaging, and Codex behavior may change; run `--check` after a Desktop update and inspect the generated log if synchronization fails.

## License

MIT. See [LICENSE](LICENSE).
