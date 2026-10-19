# Security Policy

## Privacy & Security Model

**AI Usage Tracker** is designed with privacy and security as core principles:

1. **100% Local Execution**: All quota monitoring, token checks, and metric tracking run entirely on your local machine.
2. **Zero Telemetry**: No tracking data, usage metrics, or logs are transmitted to any third-party analytics services.
3. **Local Credential Storage**: Authentication tokens are read only from their official local application locations (e.g., `~/.codex/auth.json`, `~/.claude/.credentials.json`, or the Windows Keyring via `agy`).
4. **Direct API Communication**: When communicating with provider usage APIs (such as OpenAI or Anthropic), requests are made directly from your machine to official provider endpoints over TLS/HTTPS. No proxy servers or middleman services are involved.

## Reporting a Vulnerability

If you discover a potential security vulnerability in AI Usage Tracker, please do not report it publicly on GitHub Issues.

Instead, please send an email or open a private security advisory through GitHub:
- Submit via [GitHub Security Advisory](https://github.com/LeGustaVinho/ai-usage-tracker/security/advisories)
- Or contact the maintainer directly through GitHub profile.

Please include:
- A clear description of the issue
- Steps or a minimal proof of concept to reproduce it
- Impact analysis

We will investigate and respond as quickly as possible.
