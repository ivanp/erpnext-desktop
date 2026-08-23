# Serpy

Headless Debian 13 / ERPNext v16 QEMU appliance, operated by a Windows-first .NET 10 Avalonia GUI host.

## What it is

Serpy provisions and runs a self-contained, headless Debian 13 (trixie) x86_64 VM under QEMU running the full ERPNext v16 stack (MariaDB, Redis, Frappe/ERPNext, nginx). The host product is a single per-user Avalonia desktop/tray application (`Serpy.App`) over one lifecycle core (`Serpy.Core`). ERPNext is exposed only on `http://127.0.0.1:<port>` (default 18080) via QEMU user-mode NAT.

Two-disk model:

- `system.qcow2` — replaceable OS + runtime (Python 3.14, Node 24, bench, app code, configs).
- `data.img` — persistent RAW ext4 holding the MariaDB datadir and Frappe `sites/` (incl. `site_config.json` / `encryption_key`).

## Status

The source implements U1–U7 and deterministic verification. Windows appliance build, initialization, persistence, recovery, and durability experiments remain pending the opt-in acceptance run. Serpy requires a SHA-256-verified, immutable QEMU archive under `%LOCALAPPDATA%\Serpy\runtime`; no QEMU on PATH or elevated installer is permitted. See `docs/plans/2026-08-22-0040-feat-qemu-erpnext-appliance-dotnet-avalonia-windows-host-plan.md` and `docs/results.md` for exact evidence.

- Managed QEMU/WHPX mTLS QMP smoke: **VERIFIED only for the previously installed local bundle**; archive delivery-chain validation awaits a published immutable runtime archive.
- Deterministic tests: `Serpy.Core.Tests` 153 pass, `Serpy.App.Tests` 47 pass.
- Linux x64 and macOS x64 are compile/publish-checked extension targets, not runtime-delivered platforms.

## Prerequisites (Windows)

- Windows 10 22H2 / Windows 11 x64
- **Windows Hypervisor Platform** feature enabled (Settings → Turn Windows features on or off → Windows Hypervisor Platform → reboot)
- No QEMU on PATH required — Serpy downloads, SHA-verifies, and silently installs the configured QEMU installer into `%LOCALAPPDATA%\Serpy\runtime` for the current user.

## Build & test

```bash
dotnet build Serpy.slnx -c Release
dotnet test Serpy.slnx -c Release --filter "FullyQualifiedName!~IntegrationTests"
```

WHPX integration tests (`Serpy.Windows.IntegrationTests`) are opt-in and require a WHPX-enabled machine.

## Quick start (once packaged)

1. Extract the Windows x64 distribution to a stable directory (e.g. `%LOCALAPPDATA%\Serpy`).
2. Run `Serpy.App.exe`.
3. **Build appliance** — downloads the Debian image, provisions ERPNext (~30–60 min, needs internet).
4. **Initialize** — creates the persistent data disk and first site.
5. **Start** — QEMU/WHPX starts; browser opens `http://127.0.0.1:18080`.

6. **Recover system disk** — stop the appliance, click **Recover…**, select a Serpy-built `*.qcow2` replacement, then acknowledge the irreversible migration. The core rejects an unattested image, a backing-file chain, and MariaDB/Frappe/ERPNext downgrades before it replaces `system.qcow2`; `data.img` remains in place. Copy `data.img` yourself before a major replacement.

## Operations and local data

- The dashboard is the only appliance control surface. Closing it hides it; use its tray icon to reopen it or exit.
- **Start Serpy at sign-in** creates a per-user Run-key entry that launches tray-only. Keep the portable bundle in a trusted, stable directory; disabling the setting removes only Serpy’s registry value.
- Operation logs and appliance data are stored under `%LOCALAPPDATA%\Serpy`: `logs\`, `appliance\`, `runtime\`, and `settings\`.
- QEMU is managed under `%LOCALAPPDATA%\Serpy\runtime`; no host QEMU installation or PATH entry is used.

## Architecture

| Layer | Component |
|---|---|
| GUI + tray | `Serpy.App` — Avalonia dashboard, `TrayIcon`, splash, `--tray` autostart |
| Lifecycle core | `Serpy.Core` — `IApplianceService`, state store, lifecycle lock, operations |
| Runtime | Managed QEMU bundle (WHPX, GnuTLS mTLS, slirp NAT) |
| System disk | `system.qcow2` — replaceable OS + ERPNext runtime |
| Data disk | `data.img` — persistent RAW ext4, MariaDB datadir + Frappe sites |

## Pinned versions

See `config/versions.yaml` for exact runtime/application version locks and floors.

## Results

See `docs/results.md` for verification outcomes.

## macOS / Linux

Build targets compile/publish for macOS (HVF) and Linux (KVM) in CI. Runtime verification is not delivered on those platforms; see the plan's structured extension points.
