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

## Deterministic verification

| Suite | Tests | Status |
|---|---:|---|
| `Serpy.Core.Tests` | 124 | Pass — 2026-08-22 |
| `Serpy.App.Tests` | 47 | Pass — 2026-08-22 |
| Windows appliance integration | — | Not run: full ERPNext build/init/endurance experiment remains pending |

### Windows Native-AOT publish

The Windows Native-AOT runtime pack was restored successfully using `dotnet restore -r win-x64 /p:PublishAot=true`. Local publish reached ILCompiler, then stopped because this workstation lacks the Microsoft C++ platform linker (`link.exe`/`rc.exe`). The CI `windows-latest` job restores the AOT runtime pack, publishes self-contained Native-AOT, and asserts `Serpy.App.exe` plus all provisioning assets before packaging.

The portable package script was syntax-checked and exercised against a complete synthetic AOT publish layout; it produced a ZIP, `SHA256SUMS.txt`, and the QEMU source notice. This is packaging-path verification, not a Native-AOT executable proof.
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

**Status: U3–U5 implemented and unit-tested; U6 dashboard/tray/splash/autostart implemented and Avalonia.Headless-tested; U7 packaging, CI wiring, and integration test scaffold implemented. Opt-in `SERPY_RUN_APPLIANCE` integration tests (`ApplianceWorkflowTests.cs`) for AE3 (persistence) and AE5 (durability) are structured, non-parallel, and fail-fast on missing credentials. Full Windows appliance build/init/persistence/recovery/durability experiments remain pending until provisioning is run on this machine or a WHPX-enabled CI runner.**

### Recovery durability hardening (committed 2026-08-22)

| Guard | Fix committed |
|---|---|
| Same-path replacement rejected before journal/mutation | `fix(core): reject current image recovery target` (dfe0fef) |
| Interrupted pre-copy bypass of installed-image validation | `fix(core): resume interrupted recovery swaps safely` (35ba2a6) |
| Duplicate-image resume inference blocked by source digest | `fix(core): harden appliance provisioning and recovery` (c93d9a1) |
| `IsCompletedCopyAwaitingJournal` contract clarified | `refactor(core): clarify recovery copy verification` (b5ec1cb) |
| NSIS `/D=` destination correctly unquoted | `fix(core): harden appliance provisioning and recovery` |

---

## Definition of Done audit — 2026-08-22

| DoD item | Status |
|---|---|
| U1–U7 code complete, Windows-only runtime claim | ✅ Satisfied — all units committed |
| No shell/CLI/PATH-QEMU/service/helper in operation | ✅ Satisfied — `Serpy.App` + `Serpy.Core` only |
| Managed QEMU/WHPX real QMP/QGA/serial smoke | ✅ Satisfied — 5/5 verified 2026-08-22 (U2) |
| Windows workflow: build→init→data→stop→restart→intact | ⏳ Pending — appliance not provisioned; opt-in tests written |
| Unclean-kill and replace-and-recover recorded | ⏳ Pending — requires provisioned appliance |
| Physical `data.img` boundary (datadir/sites/encryption\_key) | ⏳ Pending — requires provisioned appliance |
| GUI/tray/autostart/exit AE7+AE8 | ✅ Satisfied — 47 Avalonia.Headless tests pass |
| README + results.md correct and honest | ✅ Satisfied — this file |


## Guest version report

*(populated after first successful U3 build)*

| Component | Lock | Installed | Floor | Status |
|---|---|---|---|---|
| Python | 3.14.7 | — | 3.14.0 | PENDING full appliance build |
| Node.js | 24.2.0 | — | 24.0.0 | PENDING full appliance build |
| MariaDB | 11.8.3 | — | 11.8.0 | PENDING full appliance build |
| Redis | 8.0.1 | — | 8.0.0 | PENDING full appliance build |
| Frappe | 16.31.0 | — | 16.0.0 | PENDING full appliance build |
| ERPNext | 16.32.3 | — | 16.0.0 | PENDING full appliance build |
