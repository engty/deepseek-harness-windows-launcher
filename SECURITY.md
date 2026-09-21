# Security policy

## Scope

This repository contains a Windows launcher for DeepSeek Harness. It does not
contain user profiles, API keys, Codex credentials, DPAPI credential exports, or a
bundled development `Resources/runtime/` directory.

Never paste API keys, OAuth tokens, `auth.json`, DPAPI credential exports, or private
runtime data into an issue, pull request, log, diagnostic archive, or commit.

## Reporting

Please do not disclose an unpatched vulnerability in a public issue. Use a
private GitHub security advisory for this repository when available, or contact
the maintainers privately through the repository owner before publishing
details. Include a minimal reproduction, affected commit or release, impact,
and a suggested mitigation. Do not include live credentials in the report.

## Release security boundary

The Runtime feed uses HTTPS, artifact size checks, and SHA-256 checks. Local
release signing supports an optional Developer ID Application identity with
Hardened Runtime; the signing private key is never stored in this repository.
GitHub-hosted builds remain ad-hoc unless a separately controlled signing job
imports a short-lived certificate into a temporary keychain. Verify published
checksums before redistribution.

The default official Runtime version signal is the HTTPS npm metadata endpoint
for `@deepseek-ai/dsh`. The launcher rebuilds the candidate with the bundled
Node.js and pnpm in an App-owned staging directory; it never runs `git pull`,
modifies global package managers, or installs a Windows service.

The Windows distribution is an x64 portable bundle. Runtime dependencies are
placed beside the launcher or under the current-user application directory.
The full bundle includes an x64 Fixed Version WebView2 Runtime and loads it via
an explicit relative path. No elevation, registry write, scheduled task, service,
or machine-wide environment change is required.
