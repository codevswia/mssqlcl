# Security Policy

## Supported Versions

Security fixes are provided for the latest release series:

| Version | Supported |
| :--- | :--- |
| `1.0.x` | :white_check_mark: |

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub Issues.**

Instead, report them privately using GitHub Security Advisories:

- **Private Vulnerability Reporting:** [github.com/codevswia/mssqlcl/security/advisories/new](https://github.com/codevswia/mssqlcl/security/advisories/new)

When submitting a report, please include:
- The `mssqlcl` version (`mssqlcl --version`) and OS version.
- Clear steps to reproduce the issue (or a minimal proof of concept).
- Potential impact (for example: credential exposure, `--readonly` bypass, or privilege escalation).

You can expect an initial acknowledgment within **48–72 hours**, and we will coordinate with you on a fix before public disclosure.

## Security Model & Scope

Key security guarantees of `mssqlcl` include:
- **Credential handling:** Passwords resolved from Electron `SecretStorage` (DPAPI + AES-256-GCM) or the local DPAPI vault (`~/.mssqlcl/vault.json`) are kept in memory only and must never be written to `stdout`, `stderr`, or `~/.mssqlcl/audit.log`.
- **Read-only guard (`--readonly`):** Default MCP execution mode blocks DML, DDL, and stored-procedure execution statements unless `--readwrite` is explicitly configured.
- **Release integrity:** Official release archives include `checksums.txt` (SHA-256) verified by the installer.
