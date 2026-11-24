# Contributing to Codex Bundled Plugin Sync

Thank you for your interest in improving CodexPluginSync!

## Development Guidelines

1. **Target Framework & Dependencies**:
   - The project targets **.NET Framework 4.8** on Windows.
   - It is strictly distributed as a **single standalone executable** (`CodexPluginSync.exe`).
   - Do **not** add NuGet packages or third-party DLL dependencies.
   - Built-in OS tools (`robocopy.exe`, `cipher.exe`) and BCL assemblies are permitted.

2. **Building the Project**:
   ```powershell
   msbuild build\CodexPluginSync.csproj -p:Configuration=Release -nologo -v:m
   ```

3. **Running the Self-Test Suite**:
   Before submitting any pull request or pushing code, the built-in fixture suite must pass completely:
   ```bat
   build\bin\Release\CodexPluginSync.exe --selftest
   ```

4. **Packaging**:
   To test package generation:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build\package.ps1
   ```

5. **Design Contracts**:
   All state transitions, crash recovery matrices, backup stores, and EFS encryption requirements are frozen in [docs/DESIGN.md](docs/DESIGN.md). Always consult DESIGN.md when touching core transaction or rollback code.
