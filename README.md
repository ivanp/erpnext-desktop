# Serpy

Headless Debian 13 / ERPNext v16 QEMU appliance with a Windows-first .NET 10 + Avalonia GUI host.

## Prerequisites (Windows)

- Windows 10 22H2 / Windows 11 x64
- **Windows Hypervisor Platform** feature enabled (Settings → Turn Windows features on or off → Windows Hypervisor Platform → reboot)
- No QEMU on PATH required — the managed bundle is downloaded automatically

## Quick start

1. Extract the `Serpy-windows-x64.zip` to a stable directory (e.g. `%LOCALAPPDATA%\Serpy`)
2. Run `Serpy.App.exe`
3. Click **Build appliance** — downloads the Debian image and provisions ERPNext (~30–60 min, needs internet)
4. Click **Initialize** — creates the persistent data disk and first site
5. Click **Start** — QEMU/WHPX starts and the browser opens to `http://127.0.0.1:18080`

## Architecture

| Layer | Component |
|---|---|
| GUI + tray | `Serpy.App` — Avalonia dashboard, `TrayIcon`, splash |
| Lifecycle core | `Serpy.Core` — `IApplianceService`, state store, coordination |
| Runtime | Managed QEMU bundle (WHPX, GnuTLS mTLS, slirp NAT) |
| System disk | `system.qcow2` — replaceable OS + ERPNext runtime |
| Data disk | `data.img` — persistent RAW ext4, MariaDB datadir + Frappe sites |

## Pinned versions

See `config/versions.yaml` for exact runtime and application version locks.

## Results

See `docs/results.md` after completing the first full workflow on a WHPX-enabled machine.

## macOS / Linux

Build targets compile and publish on macOS (HVF) and Linux (KVM) runners in CI. Runtime verification is not yet delivered for those platforms. See the plan for structured extension points.
