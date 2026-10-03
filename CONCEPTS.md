# Concepts

> Shared domain vocabulary for this project — entities, named processes, and status concepts with project-specific meaning. Seeded with core domain vocabulary, then accretes as ce-compound and ce-compound-refresh process learnings; direct edits are fine. Glossary only, not a spec or catch-all.

## Appliance Lifecycle

### Appliance
The complete local ERPNext deployment instance, consisting of a replaceable OS/runtime disk and a persistent ext4 database/site disk operated by a headless QEMU virtual machine under Windows Hypervisor Platform (WHPX).
*Avoid:* VM, container, image

An Appliance moves through distinct lifecycle readiness states: NotBuilt (no OS system disk), Built (OS system disk provisioned, awaiting data initialization), and Initialized (persistent data disk formatted and Frappe site provisioned).

### System Disk
The disposable, read-only-at-runtime QEMU QCOW2 virtual disk (`system.qcow2`) containing Debian OS, Python, Node.js, MariaDB server binaries, Frappe bench, and ERPNext application code.
*Avoid:* Root disk, boot image

The System Disk contains no user transactions or database state and can be swapped or rebuilt during system recovery without data loss.

### Data Disk
The persistent, host-retained raw ext4 virtual disk image (`data.img`) hosting the MariaDB data directory and Frappe site configuration files.
*Avoid:* Storage disk, database disk

The Data Disk is mounted into the guest via in-guest bind mounts. Its presence defines whether an appliance is Initialized.

### Unattended Run
A non-interactive execution mode triggered via command-line flags (`--unattended` / `--fresh-install`) that performs automated teardown of existing appliance instances, builds and initializes the appliance with default credentials, verifies health, launches the browser, and drops into the system tray without user prompts.

## Host & Testing

### Lifecycle Lock
A named per-user synchronization primitive that serializes all mutating appliance lifecycle operations (Build, Initialize, Start, Stop, Restart, Recover, Reset) within the host core, scoped by the application data directory to ensure test isolation.

### UI Test Fixture
An isolated desktop testing harness that launches the real compiled host executable within a sandboxed temporary directory, automating UI interactions via Windows UIAutomation (FlaUI) and capturing visual screenshots and element tree dumps on failure.
