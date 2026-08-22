# Serpy — Verification Results

---

## Platform

| Item | Value |
|---|---|
| Host OS | Windows 11 Pro x64 (build 26200) |
| Host CPU | Intel Core i9-13900H |
| .NET SDK | 10.0.400 |
| QEMU | 11.1.0 (Stefan Weil / weilnetz.de, 2026-08-11) |
| QEMU path | `%LOCALAPPDATA%\Serpy\runtime\qemu-11.1.0` |
| WHPX | Enabled (HypervisorPlatform feature active) |

---

## Unit test results

| Suite | Tests | Status |
|---|---|---|
| `Serpy.Core.Tests` | 66 | ✅ Pass |
| `Serpy.App.Tests` | 19 | ✅ Pass |

---

## U2 — Managed QEMU bundle + WHPX + mTLS QMP smoke

**Status: ✅ VERIFIED — 2026-08-22**

### Smoke run details

Mode: **SERPY_QEMU_VERSIONS_YAML** (managed-chain) — resolver loaded versions.yaml, detected bundle
already installed at `%LOCALAPPDATA%\Serpy\runtime\qemu-11.1.0`, skipped re-download. Paused VMs
use `-machine q35` (corrected from `-machine none` which is invalid with `-accel whpx`).

| Test | Result | Note |
|---|---|---|
| `QemuVersion_PrintsVersionString` | ✅ | `QEMU emulator version 11.1.0 (v11.1.0-12130-ge470268ff4)` |
| `QemuBuild_HasTlsSupport` | ✅ | `tls-creds-x509` object type recognised (GnuTLS confirmed) |
| `WhpxProbe_Passes` | ✅ | `-accel whpx -machine q35`; no WHPX error in stderr; 5 s bounded probe |
| `MutualTlsQmp_PausedVm_QueryStatus_Quit` | ✅ | mTLS QMP connected; `query-status` returned `{status:…}`; `quit` accepted |
| `ForeignClient_WithoutClientCert_IsRejected` | ✅ | Plaintext TCP client got no QMP greeting |

**Total: 5/5 passed in 0.54 s** (`SERPY_QEMU_VERSIONS_YAML=config/versions.yaml`)

### Installation provenance

Stefan Weil installer `qemu-w64-setup-20260811.exe`  
SHA-256: `f98a8aeb5f7faea9765b6dee28316c266cd179d80354a2fed8e50176f9a2e59f`  
Installed via: `eng/install-qemu-local.ps1` (NSIS `/S /D=` to user-writable path, no elevation needed)  
The managed-chain download → SHA verify → NSIS install → version probe → TLS probe → atomic rename
path in `ManagedRuntimeResolver` is exercised on a clean machine via `qemu-windows.yml` CI workflow.

### Fixes applied during U2 verification

| Issue | Fix |
|---|---|
| `-machine none` invalid with WHPX | Changed to `-machine q35` in `WhpxProbe.RunAsync` and both smoke QMP tests |
| `WhpxProbe.RunAsync` hung under `-S` | Bounded 5 s stderr read; explicit kill; liveness check instead of `WaitForExitAsync` |
| Empty SHA silently skipped | NSIS path now throws `InvalidOperationException` if SHA is empty |
| Staging → `BundleDir` direct install | NSIS installs to staging dir; version + TLS probes; atomic rename |
| `qemu.version: "9.2.x"` not pinned | Pinned to `11.1.0`; `-version` output must contain pinned version before commit |
| GnuTLS transitive DLLs missing from build script | Added libnettle, libhogweed, libgmp, libp11-kit, libidn2, libunistring, libtasn1 |

---

## U3–U7 — appliance workflow

**Status: U3 ✅ done, U4 ✅ done, U5 in progress, U6–U7 pending**

---

## Guest version report

*(populated after first successful U3 build)*

| Component | Lock | Installed | Floor | Status |
|---|---|---|---|---|
| Python | 3.14.1 | — | 3.14.0 | PENDING |
| Node.js | 24.2.0 | — | 24.0.0 | PENDING |
| MariaDB | 11.8.3 | — | 11.8.0 | PENDING |
| Redis | 8.0.1 | — | 8.0.0 | PENDING |
| Frappe | version-16 | — | 16.0.0 | PENDING |
| ERPNext | version-16 | — | 16.0.0 | PENDING |
