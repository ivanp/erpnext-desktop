# Phase 1 — QEMU ERPNext Appliance Prototype

## Objective

Build a minimal Linux prototype proving that ERPNext can run as a self-contained headless Linux appliance under QEMU.

Phase 1 is **not** a desktop application. Do not implement Wails/Svelte, system tray, Windows/macOS support, installers, or sophisticated VM management yet.

Target architecture:

```text
Linux x86_64 Host
       │
       ▼
QEMU + KVM
       │
       ▼
Debian x86_64 VM
       │
       ├── MariaDB
       ├── Redis
       ├── Frappe
       ├── ERPNext
       └── nginx/web server
               │
               ▼
        Persistent ext4 disk
```

The host must be able to start the appliance and access ERPNext through `localhost`.

---

## Design Goals

Prioritize:

1. Stability
2. Simplicity
3. Reproducibility
4. Minimal moving parts
5. Portability to QEMU/WHPX on Windows and QEMU/HVF on macOS later

Prefer boring, well-supported solutions over clever optimizations.

Do not introduce Docker, Podman, Kubernetes, or other container runtimes unless technically required.

---

## Host Requirements

Initial development target:

* Linux x86_64
* QEMU
* KVM hardware acceleration
* CLI/scripts only

QEMU must be treated as the virtualization boundary. Avoid architecture that depends unnecessarily on Linux-specific host functionality.

Future platforms will use:

```text
Linux   → QEMU/KVM
Windows → QEMU/WHPX
macOS   → QEMU/HVF
```

Do not implement Windows/macOS support in Phase 1.

---

## VM Architecture

Use two virtual disks.

### System Disk

```text
system.qcow2
```

Contains:

* minimal Debian
* Python
* Node.js
* Frappe Bench
* ERPNext
* MariaDB
* Redis
* nginx or required web frontend
* required system services
* appliance management/startup scripts

Treat the system disk as replaceable application/runtime infrastructure.

### Data Disk

```text
data.img
```

Use:

* sparse RAW image
* ext4 filesystem
* attached to QEMU as a VirtIO block device
* mounted inside the VM at `/data`

Persistent ERP data must live here.

Expected structure:

```text
/data/
├── mariadb/
├── frappe/
│   └── sites/
└── config/
```

Exact internal organization may be adjusted if required by Frappe/MariaDB.

The important invariant is:

> Replacing `system.qcow2` must not destroy customer/company data.

---

## ERPNext Installation

Install ERPNext directly inside Debian using the standard Frappe/Bench stack.

Do not use containers.

Pin major runtime dependencies where appropriate and document installed versions.

Automate provisioning sufficiently that the appliance can be rebuilt reproducibly.

The coding agent may choose the appropriate mechanism, e.g.:

* shell scripts
* cloud-init
* image-building scripts
* configuration-management tooling

Prefer the simplest maintainable solution.

---

## Frappe Sites

Use Frappe's normal site/database model.

The architecture must eventually support multiple companies as separate Frappe sites:

```text
ERP appliance
├── company-a site
├── company-b site
└── company-c site
```

For Phase 1, creating **one working site** is sufficient.

Do not implement a company-management UI.

---

## Networking

Keep networking minimal.

ERPNext must not require the guest VM to be directly accessible from the LAN.

Use QEMU user-mode/NAT networking with host port forwarding.

Example conceptual mapping:

```text
Host
127.0.0.1:18080
       │
       ▼
QEMU NAT
       │
       ▼
Guest HTTP service
```

After starting the appliance, this must work from the Linux host:

```text
http://127.0.0.1:18080
```

Avoid bridged networking.

---

## QEMU Configuration

Keep virtual hardware minimal.

Required:

* x86_64
* KVM acceleration
* CPU
* RAM
* system disk
* data disk
* VirtIO storage where practical
* network
* headless operation
* serial console useful for diagnostics

Do not add unnecessary virtual hardware such as:

* graphics/GPU
* audio
* USB
* Bluetooth
* clipboard integration
* host directory sharing

The appliance is a headless server.

Keep QEMU invocation/configuration isolated so the accelerator can later change from:

```text
KVM → WHPX / HVF
```

without redesigning the VM.

---

## Lifecycle Scripts

Provide simple host-side commands/scripts for at least:

```text
build
start
stop
status
```

For example, the exact CLI design is up to the implementation:

```bash
./erp-appliance build
./erp-appliance start
./erp-appliance status
./erp-appliance stop
```

A sophisticated Go management application is **not required in Phase 1**.

Graceful shutdown should be preferred over killing QEMU.

---

## Persistence Test

Persistence is a critical Phase 1 acceptance criterion.

Verify:

1. Start appliance.
2. Open ERPNext.
3. Create identifiable test data.
4. Shut the VM down cleanly.
5. Start it again.
6. Confirm the data remains.
7. Confirm MariaDB and ERPNext recover normally.

Also test an unclean QEMU termination and subsequent restart.

The objective is not to guarantee crash-proof operation yet, but document observed recovery behavior.

---

## Replaceable-System Test

Demonstrate that persistent data is sufficiently separated from the system disk.

Desired experiment:

```text
system.qcow2 (A)
      +
data.img
      ↓
working ERPNext

replace system disk

system.qcow2 (B)
      +
same data.img
      ↓
recover existing ERPNext site/data
```

It is acceptable if attaching a fresh system image requires a controlled recovery/migration command.

Document the required process.

Do not compromise Frappe/MariaDB correctness merely to make the system disk trivially interchangeable.

---

## Data Safety

Do not treat VM disk snapshots as the eventual accounting backup strategy.

The future product will perform application-aware backups of:

* MariaDB/Frappe database
* site files
* required site configuration

and store backups **outside `data.img`**.

Full backup/restore UX is outside Phase 1.

However, structure the appliance so application-level backup/restore can be added later.

---

## Portability Constraints

While implementing Phase 1, assume the same appliance concept will eventually run on:

```text
Windows x86_64 → QEMU/WHPX → Debian amd64
macOS ARM64    → QEMU/HVF  → Debian arm64
```

Therefore:

* avoid unnecessary dependency on host Linux filesystems
* avoid host bind mounts for persistent ERP data
* avoid Linux bridge networking
* avoid `/dev/kvm` access outside the QEMU-launching layer
* keep host/guest communication generic
* keep persistent state on virtual block storage
* avoid assumptions that prevent building an ARM64 appliance later

ARM64 does **not** need to be implemented in Phase 1.

---

## Explicitly Out of Scope

Do not implement:

* Windows support
* macOS support
* ARM64 appliance
* Wails
* Svelte
* desktop GUI
* system tray/menu bar
* Windows Service
* macOS launchd integration
* installer
* auto-update
* QMP management layer beyond what is useful for the prototype
* LAN/multi-user exposure
* automatic backup scheduler
* cloud backup
* Docker/Podman
* VM GUI
* sophisticated monitoring

Avoid expanding Phase 1 into a production product.

---

## Deliverables

Produce:

1. Scripts/source required to build the Debian appliance.
2. QEMU configuration/start script.
3. Persistent `data.img` creation/initialization mechanism.
4. Start/stop/status commands.
5. Automated or documented ERPNext provisioning.
6. README containing:

   * host prerequisites
   * build instructions
   * startup instructions
   * ERPNext URL
   * shutdown instructions
   * disk architecture
   * troubleshooting/log locations
7. Results of persistence and system-disk replacement tests.

Do not commit generated large VM images to Git unless explicitly appropriate. Build/download them through the project's setup tooling.

---

## Acceptance Criteria

Phase 1 is complete when this workflow reliably works:

```text
git clone project
       ↓
install documented host prerequisites
       ↓
build/provision appliance
       ↓
start appliance
       ↓
open http://127.0.0.1:<port>
       ↓
login to ERPNext
       ↓
create accounting/test data
       ↓
stop appliance
       ↓
start appliance
       ↓
data is intact
```

The resulting architecture should make the next phase possible without redesigning the appliance:

```text
Go appliance manager
       ↓
QEMU
       ↓
same Debian appliance
       ↓
same persistent data model
```

The primary Phase 1 question to answer is:

> **Can ERPNext be packaged as a reliable, reproducible, headless QEMU appliance whose runtime and persistent business data are cleanly separated?**
