# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 1.0.x   | :white_check_mark: |
| < 1.0   | :x:                |

## Security Model & Data Privacy

Codex Bundled Plugin Sync operates strictly as a local utility:
- **No telemetry, tracking, or network transmission of user data**: The only network request made is an optional, read-only GET request to the public GitHub Releases API (`api.github.com`) to check for newer published releases.
- **Source bundle isolation**: Source files inside the WindowsApps AppX package are accessed read-only.
- **Redaction by default**: When exporting diagnostic packages via `--export-diag`, usernames, computer names, file paths, and SIDs are automatically redacted.
- **EFS Encryption Safety**: Windows NTFS EFS encrypted source files are preserved using native cipher/robocopy protocols without dropping security attributes.

## Reporting a Vulnerability

If you discover a security issue or vulnerability, please do not open a public issue. Instead, please report it privately to the repository maintainer through GitHub Security Advisories or by contacting the maintainer directly.
