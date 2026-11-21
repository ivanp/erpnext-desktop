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
| `Serpy.Core.Tests` | 498 | Pass — 2026-11-21 |
| `Serpy.App.Tests` | 153 | Pass — 2026-11-21 |
| `Serpy.InstallerBootstrapper.Tests` | 20 | Pass — 2026-11-21 |
| Linux x64 publish | compile/publish assets | Pass locally — runtime acceptance not claimed |
| macOS x64 publish | compile/publish assets | Pass locally — runtime acceptance not claimed |
| Windows appliance integration | AE3, AE5 clean run | Pass — 2026-11-21: Clean Build → Initialize → AE3 (persistence across clean restart) → AE5 (durability after unclean QEMU kill). |

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

### Per-user installer delivery status

The user approved the Stefan Weil NSIS installer as the runtime delivery mechanism. `ManagedRuntimeResolver` downloads it over HTTPS, verifies SHA-256 `f98a8aeb5f7faea9765b6dee28316c266cd179d80354a2fed8e50176f9a2e59f`, installs it silently into a same-volume staging directory under the current user’s `%LOCALAPPDATA%\Serpy\runtime`, validates files/version/GnuTLS, and atomically commits it with the validation marker. No QEMU PATH dependency is used. The clean installer delivery chain and appliance workflow remain pending.

| Issue | Fix |
|---|---|
| Installer output could bypass validation | Installer targets staging, then follows the same validation and atomic commit path as archives. |
| Installer SHA allowed empty | Installer install rejects a missing SHA-256 before execution. |
| Archive skipped binary/TLS probes | Both delivery modes validate contents, version, and GnuTLS support before replacement. |
| Stale files accepted as an installed bundle | Reuse requires an atomically written marker matching the delivery SHA-256 and QEMU version. |
| Replacement move fails | Existing bundle is renamed to a same-volume backup and restored on failed staging move. |

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

---

## Unattended Clean Installation & Windows Desktop GUI E2E Automation — 2026-09-18

| Deliverable | Status | Verification Evidence |
|---|---|---|
| Unattended Clean Install (`--unattended` / `--fresh-install`) | ✅ Satisfied | Automated reset pipeline stops running VMs, cleans instance disks, preserves downloaded runtime & base Debian image cache, provisions with default credentials (`Administrator` / `admin`), and opens browser |
| Windows Console Attachment | ✅ Satisfied | Native-AOT compatible `AttachConsole(-1)` routes stdout/stderr directly to calling terminal |
| Mutex Takeover & Process Guard | ✅ Satisfied | Sandboxed `SERPY_TEST_APPDATA` mutex scoping prevents test/production collision; graceful/kill takeover handles prior instances |
| FlaUI Windows Desktop UI Tests (`Serpy.Windows.UiTests`) | ✅ Satisfied | Real OS desktop UI automation testing DashboardWindow, InitializeDialog input validation, Exit dialog, and lifecycle state transitions |
| Diagnostic Failure Artifacts (`DiagnosticCapture`) | ✅ Satisfied | Automatically captures desktop + window PNG screenshots, UIA visual tree dump (`uitree.txt`), and application logs on any test failure |


## Guest version report

*(Verified on 2026-11-21 from clean appliance build and initialization)*

| Component | Lock | Installed | Floor | Status |
|---|---|---|---|---|
| Python | 3.14.7 | 3.14.7 | 3.14.0 | ✅ PASS |
| Node.js | 24.2.0 | 24.2.0 | 24.0.0 | ✅ PASS |
| MariaDB | 11.8.3 | 11.8.3 | 11.8.0 | ✅ PASS |
| Redis | 8.0.2 | 8.0.2 | 8.0.0 | ✅ PASS |
| Frappe | 16.31.0 | 16.31.0 | 16.0.0 | ✅ PASS |
| ERPNext | 16.32.3 | 16.32.3 | 16.0.0 | ✅ PASS |

---

## Offline build proof (U13, U16)

*(Run 16, 2026-11-21, host build through `BuildOperation` with `restrict=on`; inputs from the download cache, packed into a transport disk.)*

### Verdict

- **Proven:** the build half of R20. The guest built the full runtime from the cache with outbound network blocked and passed the six-component version gate. Total build time 212 s (3.5 min) on a warm cache. Evidence in `.artifacts/dev/proof/run16.log`.
- **Not yet proven:** AE10 (two builds identical). It needs a second build to compare, and none has run.

### Restricted-network evidence (from the guest, run 16)

- `curl -m 5 https://deb.debian.org` failed with exit code 6.
- `getent hosts deb.debian.org` failed with exit code 2.
- The `bench` asset step logged `Temporary failure in name resolution` for `github.com` and continued, as expected when the network is blocked.

After the stage the host deleted `.cache-transport.tar`, `.scratch.raw` and `.cloud-init-seed.iso`. The accepted `system.qcow2` was written with `.system-accepted.json`.

### Version gate (run 16 output)

The guest reported `python=3.14.7 node=24.2.0 mariadb=11.8.3 redis=8.0.2 frappe=16.31.0 erpnext=16.32.3`. These equal the locks in `config/versions.yaml`.

Frappe and ERPNext are pinned by the commit id inside their GitHub archives (`git get-tar-commit-id`) and by the archive SHA-256 in `config/build-inputs.json`. The guest repository `HEAD` is a commit synthesized locally from the archive, so it does not carry the upstream hash. Earlier "HEAD (6a329d0)" rows came from the collection VM and are not evidence for this build.

### Deviations from upstream

1. **`banking/yarn.lock`:** ERPNext 16.32.3 ships a lockfile for `banking/` that lacks entries for `follow-redirects@^1.16.0` and `proxy-from-env@^2.1.0`, which `axios@1.18.1` declares. `guest/provision.sh` appends the two entries, using the already-cached 1.16.3 and 2.1.0 tarballs, before the bare repository is created. Online, yarn resolves them silently; with a frozen offline lockfile it refuses.
2. **`pdfplumber 0.11.10` with `pillow 12.2.0`**, and `flit_core 3.11.0`: pinned in the wheelhouse so the resolver does not need a version that is absent from the cache.
3. **Synthesized git refs:** git sources are GitHub archives turned into bare repositories at build time. The branch and tag names, and a ref named with the commit sha, are created locally so `bench get-app --branch` and the `pyproject.toml` git pins resolve. `air-datepicker` is served this way too.
4. **`pypika` and `gunicorn`:** pinned through `/etc/serpy-python-overrides.txt` (`UV_OVERRIDE`, `PIP_CONSTRAINT`) to local `file://` repositories; `pyproject.toml` is not edited.

### Mechanisms that made the offline build work

1. **`apt-ftparchive` is in the base image** (`apt-utils`), so the guest indexes the deb pool itself. `dpkg-scanpackages` is absent.
2. **One flat deb repository:** `deb [trusted=yes] file:/srv/cache/debs ./` holds all 244 debs (Trixie, Sid Python 3.14, MariaDB 11.8.3). Pinning is not needed because the pool holds only the target versions. Offline installs name exact versions without `-t sid`, which a flat repository cannot honour.
3. **Pre-built CPython 3.14 wheels** for `mysqlclient` and `psutil` are in the wheelhouse, so `pip`/`uv` with `--no-index` never needs a build backend.
4. **git `insteadOf` rewrites** in `/etc/gitconfig` send `github.com/frappe/*` to the local repositories. `safe.directory` entries are added for those paths. The rewrites are removed when the stage ends.
5. **Yarn offline mirror:** `yarn-offline-mirror /srv/cache/yarn-mirror` with `--install.offline` installs all packages with no network.
6. **Environment across `su - frappe`:** `su -` clears the environment, so the offline flags (`PIP_NO_INDEX`, `PIP_FIND_LINKS`, `UV_NO_INDEX`, `UV_FIND_LINKS`, `UV_OFFLINE`) are written to `/etc/profile.d/serpy-offline.sh` and `/etc/serpy-offline.env`.

### First start on the run 16 image (2026-11-21)

**Passed.** `PersistenceAcrossCleanRestart` ran from the Built state of the run 16 image, on port 18181 under an isolated app-data root. Log: `.artifacts/dev/proof/init-start.log`; per-operation logs under `.artifacts/dev/proof/logs`.

| Step | Result |
|---|---|
| Initialize | Site `site1.local` initialized, `data.img` committed; 18:13:31 to 18:15:40 (about 2 min 10 s) |
| First Start | Functional health gate passed; running 18:16:29 to 18:16:51 (22 s) |
| Record written, clean stop, second Start | Record `pneqkve2g3` survived the clean restart |

So nothing in the shipped image depends on the `/srv` mount, the `insteadOf` rewrites, `.yarnrc` or `/etc/serpy-offline.env` that the stage removes. This also covers the start half of AE9. It does not cover a cold-cache Welcome-to-Ready run or the MSI install.

### Second run downloads nothing (engine level, 2026-11-21)

With the populated proof cache and an HTTP handler that fails on every request, `DownloadEngine` made **0 requests** and reported every part `alreadyPresent` (6 parts, 1,373 files, 15 s to verify hashes). Scope: the six parts the engine downloads (`database`, `system-tools`, `language-tools`, `web-tools`, `framework`, `erpnext`). The `engine` (QEMU) and `os` (Debian base image) parts go through the installer and `BaseImageDownloader`, so this run does not show they hit the cache. The file counts it reported (language-tools 179, framework 4) differ from the generator README table (178 and 3); that is unresolved.

### Pending: needs a person or a longer run

| Item | Why it is not done |
|---|---|
| Generator output diffed against the committed `config/build-inputs.json` | `eng/lock/generate-build-inputs.sh` does not run on a stock `debian:trixie` image on this PC: the image carries packages newer than the 2026-08-22 snapshot, so the pinned `-dev` installs fail on exact-version dependencies. Four attempts (downgrade flags, pin priorities, clean distros) ended in a different dependency conflict each time (the last: `python3.14` needs `libc6 >= 2.42`). The committed list is verified by the offline build, not by regeneration; regenerating may not reproduce it. Next step is the `generate-build-inputs.yml` workflow in CI. |
| Timed cold-cache Welcome-to-Ready run | Needs a real 1.3 GB download plus the full guided setup. The Welcome screen keeps "about 1 hour" until this is measured. |
| Clean install from the MSI to the sign-in page | Needs the reference PC and a Windows elevation prompt. |
| Observed first-time user install | Needs a person who has installed Office and never used Serpy. |
