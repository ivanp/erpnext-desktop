# AGENTS.md

Guidance for AI coding agents working in this repository.

## Project

Serpy — a headless Debian 13 / ERPNext v16 QEMU appliance operated by a Windows-first .NET 10 Avalonia GUI host. The host is a single per-user desktop/tray application (`Serpy.App`) over one lifecycle core (`Serpy.Core`). ERPNext is exposed only on `http://127.0.0.1:<port>` (default 18080) via QEMU user-mode NAT.

Authoritative plan: `docs/plans/2026-08-22-0040-feat-qemu-erpnext-appliance-dotnet-avalonia-windows-host-plan.md`. Verification outcomes: `docs/results.md`.

## Status

- U1–U5 complete: solution scaffold, managed QEMU/WHPX bundle + mTLS QMP/QGA, cloud-init build pipeline, persistent data init, lifecycle operations.
- U6 in progress: Avalonia dashboard, tray, splash, `--tray` autostart, Run-key registration.
- U7 pending: endurance verification, packaging, CI, operator docs.

## Architecture

| Layer | Component |
|---|---|
| GUI + tray | `src/Serpy.App` — Avalonia dashboard, `TrayIcon`, splash, `--tray` autostart |
| Lifecycle core | `src/Serpy.Core` — `IApplianceService`, state store, lifecycle lock, operations |
| Runtime | Managed QEMU bundle (WHPX, GnuTLS mTLS, slirp NAT) |
| System disk | `system.qcow2` — replaceable OS + ERPNext runtime |
| Data disk | `data.img` — persistent RAW ext4, MariaDB datadir + Frappe sites |

Key source layout (`src/Serpy.Core/`):

- `Contracts/` — `IApplianceService` (Build/Initialize/Start/Stop/Restart/Recover/Status), `ApplianceStatus`, `OperationResult`, `OperationUpdate`.
- `Operations/` — `ApplianceService` (composition root, serializes mutations through `LifecycleLock`) plus one operation class per verb.
- `Qemu/` — `AcceleratorPolicy` (table-driven WHPX/KVM/HVF), `QemuArguments`, `QemuProcess`, `ManagedRuntimeResolver` (SHA-verified bundle), `WhpxProbe`, `TlsCertificateStore`.
- `Protocols/` — `QmpClient`, `QgaClient`, `SerialClient` (TCP loopback, mTLS).
- `Images/` — `BaseImageDownloader`, `NoCloudSeedWriter`, `QemuImageTool`, `SystemImageManifest`.
- `Versions/` — `VersionManifestLoader`, `VersionEvaluator`, `VersionGate` (R9 lock/floor checks).
- `Health/` — `HealthChecker`, `HealthCredentials` (R11 functional gate).
- `Coordination/` — `LifecycleLock`, `StateStore`, `ApplianceState`, `ProcessIdentity`.
- `Configuration/` — `ApplianceSettings`, `KnownPaths`.

## Invariants (do not break)

- **Lifecycle rules live in `Serpy.Core` only.** ViewModels must never spawn QEMU, write state, or parse QMP. All mutating operations serialize through `LifecycleLock`; status reads do not acquire it.
- **Two-disk boundary.** MariaDB datadir and Frappe `sites/` (incl. `site_config.json` / `encryption_key`) must live physically on `data.img`; `system.qcow2` is disposable. `init` commits the final disk name only after the health gate passes and the guest halts cleanly.
- **Recovery guards.** `recover` rejects MariaDB/Frappe/ERPNext downgrades before mutation; runs `mariadb-upgrade` only when needed, then `bench migrate`; a durable recovery journal blocks normal `start` until a compatible `recover` completes the health gate.
- **Managed QEMU, no PATH dependency.** QEMU is SHA-verified and installed under `%LOCALAPPDATA%\Serpy\runtime`; never rely on QEMU on PATH.
- **Windows-first honesty.** Windows x64/WHPX is the delivered, runtime-verified platform. macOS (HVF) / Linux (KVM) are compile/publish-checked extension targets only — do not claim runtime verification for them.
- **Credentials.** ERP health credentials live in the current-user Windows secret store (DPAPI), keyed by workspace; never pass them as command-line arguments or persist them in state/logs.

## Build & test

```bash
dotnet build Serpy.slnx -c Release
dotnet test Serpy.slnx -c Release --filter "FullyQualifiedName!~IntegrationTests"
```

- `Directory.Build.props`: net10.0, nullable, `TreatWarningsAsErrors`, central package management, `IsAotCompatible`.
- `Serpy.App` publishes Native-AOT (`PublishAot=true`) for Windows x64 — keep AOT compatibility (no unsupported reflection).
- WHPX integration tests (`tests/Serpy.Windows.IntegrationTests`) are opt-in and require a WHPX-enabled machine; they are excluded from the default filter.
- `config/versions.yaml` holds exact locks and floors; `Serpy.Core` parses/validates it at build/init time (R9).

## Conventions

- Follow the plan's requirement IDs (R1–R17), key decisions (KD1–KD7), and implementation units (U1–U7) when touching behavior.
- Update `docs/results.md` when verification outcomes change.
- Guest helper scripts live in `guest/` (`init-data.sh`, `recover.sh`, `provision-done.sh`); cloud-init seeds in `build/cloud-init/`.
- CI: `build-test-publish.yml` (build/test + Windows Native-AOT publish), `qemu-windows.yml` (self-hosted WHPX runner, QEMU bundle build + mTLS smoke).

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **erpnext-desktop** (1600 symbols, 4068 relationships, 134 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> Index stale? Run `node .gitnexus/run.cjs analyze` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? `npx gitnexus analyze` (npm 11 crash → `npm i -g gitnexus`; #1939).

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows. For regression review, compare against the default branch: `detect_changes({scope: "compare", base_ref: "main"})`.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `query({search_query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `context({name: "symbolName"})`.
- For security review, `explain({target: "fileOrSymbol"})` lists taint findings (source→sink flows; needs `analyze --pdg`).

## Never Do

- NEVER edit a function, class, or method without first running `impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `rename` which understands the call graph.
- NEVER commit changes without running `detect_changes()` to check affected scope.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/erpnext-desktop/context` | Codebase overview, check index freshness |
| `gitnexus://repo/erpnext-desktop/clusters` | All functional areas |
| `gitnexus://repo/erpnext-desktop/processes` | All execution flows |
| `gitnexus://repo/erpnext-desktop/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->
