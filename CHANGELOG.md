# Changelog

## 1.0.0 - 2026-11-24

### Added

- WinForms desktop interface for status checks, synchronization, rollback, cleanup, and diagnostic export.
- Pure WinExe subsystem for `CodexPluginSync.exe` — completely eliminates black console window flashing on double-click launch.
- Dedicated `CodexPluginSync.Cli.exe` console binary and `sync-codex-bundled.cmd` wrapper for headless, task-scheduler, and CI automation (`Cli.exe` with no args outputs usage help directly without opening GUI).
- Unified `ProcessRunner` layer ensuring all child processes (PowerShell, robocopy, cipher, cmd mklink) run 100% hidden with no background window popups, process-tree termination on timeout (`taskkill /F /T /PID`), and robust robocopy exit code & sharing-violation handling.
- Four-section GUI layout: core environment status header, detailed component status table, action controls with instant file-level cancellation support, and realtime progress/logging area.
- Standalone `sync-codex-bundled.cmd` wrapper with disabled delayed expansion and direct `%*` parameter forwarding, calling the exe directly with 2->0 exit-code mapping on `--check`. Note: no-argument invocation pauses for double-click visibility; automated/headless no-argument tasks should pass `--no-pause` or set `CODEX_NO_PAUSE=1` (explicit command arguments like `--check` or `--sync` exit without pause).
- Dual entry point release packaging (GUI + CLI, zero external DLL dependencies) through `build/package.ps1`.
- Background GitHub Releases check in the GUI, showing the current version and a newer published version when available, with a dedicated "下载新版本" button navigating directly to GitHub Releases.
- System tray minimization and periodic background checks (M4):
  - Minimize to system tray (`NotifyIcon`) on window close (`X`) or minimize, with informational balloon tip on first hide.
  - Tray context menu: "打开主窗口", "立即检查", "立即同步", "-", "访问 GitHub 主页", "退出".
  - Clean exit option disposing unmanaged tray icon and timer resources.
  - Periodic background silent checks (configured via `Settings.AutoCheckHours`, initial 2-minute delay) with balloon tip notifications when updates are detected.
  - Single-instance mutex protection for GUI mode preventing duplicate tray instances.
- Headless CLI commands for `--check`, `--sync`, `--rollback`, `--cleanup-only`, `--export-diag`, and `--selftest`.
- Component snapshot backups, crash recovery journal, SHA-256 verification, EFS handling, native-host path checks, and redacted diagnostic archives.

### Verified

- Built with the in-box .NET Framework MSBuild workflow.
- Release packaging requires a passing `--selftest` run.
- Fixture coverage and real-machine acceptance evidence are recorded in [docs/M1-STATUS.md](docs/M1-STATUS.md).

### Known limits

- The target machine must already have .NET Framework 4.8.
- GitHub version checking is optional; network failures do not block local checks or synchronization.
