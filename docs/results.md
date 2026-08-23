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

| Suite | Tests / scope | Status |
|---|---:|---|
| `Serpy.Core.Tests` | 152 | Pass — 2026-08-23 |
| `Serpy.App.Tests` | 47 | Pass — 2026-08-23 |
| Linux x64 publish | compile/publish assets | Pass locally — runtime acceptance not claimed |
| macOS x64 publish | compile/publish assets | Pass locally — runtime acceptance not claimed |
| Windows appliance integration | 2 opt-in guards | Full build/init/endurance experiment blocked: no immutable QEMU archive URL/SHA-256 and required `SERPY_ADMIN_PASSWORD` / `SERPY_RUN_APPLIANCE` are unset |

### Windows Native-AOT publish

The Windows Native-AOT runtime pack restored successfully using `dotnet restore -r win-x64 /p:PublishAot=true`. On 2026-08-23, local publish reached ILCompiler and stopped because this workstation lacks the Microsoft C++ platform linker (`link.exe`/`rc.exe`); `vswhere` found no installation with `Microsoft.VisualStudio.Component.VC.Tools.x86.x64`. The CI `windows-latest` job restores the AOT runtime pack, publishes self-contained Native-AOT, and asserts `Serpy.App.exe` plus all provisioning assets before packaging.

`Serpy.App` build output contains every provisioning asset (`config/versions.yaml`, NoCloud seed templates, and guest helpers), proven by `PackagingAssetTests`. Native-AOT executable proof remains CI-owned until the Desktop C++ workload is installed locally.

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

### Archive delivery status

The former Stefan Weil NSIS installer requires elevation and cannot satisfy the
per-user, no-privileged-helper product contract. It is no longer a configured
runtime source. The resolver now accepts only a SHA-256-pinned QEMU archive,
probes its version and GnuTLS support before commit, and rejects an empty
archive descriptor. The repository has no immutable archive release asset yet,
so end-to-end appliance verification is blocked until release publishing supplies
the archive URL and SHA-256.

### Archive runtime hardening

| Issue | Fix |
|---|---|
| NSIS installer requires elevation | Removed installer selection and legacy manifest fields; archive-only resolver is per-user. |
| Archive SHA allowed empty | Archive installs reject a missing SHA-256 before download. |
| Archive skipped binary/TLS probes | Archive validates contents, version, and GnuTLS support before replacement. |
| Stale files accepted as an installed bundle | Reuse requires an atomically written marker matching the archive SHA-256 and QEMU version. |
| ZIP layout could retain `qemu.zip` or fail on top-level directory | Extracts into a separate temporary tree and accepts one expected top-level bundle root. |
| CI smoke patched installer hash | Smoke temporary manifest now patches only `archiveUrl` and `archiveSha256`. |

### Guest package-closure preflight

Before it downloads the Debian base image or starts QEMU, `BuildOperation` resolves the locked Redis, Python 3.14, MariaDB, Node, Frappe, ERPNext, and `frappe-bench` inputs from their recorded immutable sources. Frappe and ERPNext tags are dereferenced recursively through GitHub's Git-object API and must resolve to their configured commits; guest cloud-init then checks out and verifies those commits before asset build. A resolution failure is reported as a host-side package-closure failure rather than a guest provisioning failure. The deterministic fixture tests cover successful lightweight and annotated tags, tag/commit mismatch, and a missing exact apt package; live source endpoint checks returned HTTP 200 on 2026-08-23.

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
| Redis | 8.0.2 | — | 8.0.0 | PENDING full appliance build |
| Frappe | 16.31.0 | — | 16.0.0 | PENDING full appliance build |
| ERPNext | 16.32.3 | — | 16.0.0 | PENDING full appliance build |
