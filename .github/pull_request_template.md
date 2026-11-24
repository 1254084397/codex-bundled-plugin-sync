## Description
Please include a summary of the changes and the related issue/feature.

## Verification Checklist
- [ ] Built with MSBuild targeting .NET Framework 4.8.
- [ ] Single-exe constraint preserved (no third-party DLL dependencies introduced).
- [ ] Run `CodexPluginSync.exe --selftest` and all test cases pass.
- [ ] If changing transaction or rollback logic, reviewed against contracts in `docs/DESIGN.md`.
- [ ] Documentation and comments updated if necessary.
