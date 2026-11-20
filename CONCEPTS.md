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

### Guided Setup
The first-run sequence that takes a PC from installed to Ready without technical input: PC check, downloads, build, company data, start.
*Avoid:* Build & Initialize, provisioning wizard

Guided Setup reuses existing company data without asking and resumes at the interrupted step after a failure or restart.

### Ready
The state in which ERPNext is running and has passed its sign-in health check; the only state in which Serpy tells the user ERPNext can be opened.
*Avoid:* Healthy, up

### Leftover
A file that an unfinished setup step left behind, such as a staging system disk, a pending data disk or a partial download. The step that owns a Leftover deletes it without asking; committed company data and verified Download Cache files are never Leftovers.

## Host & Testing

### Lifecycle Lock
A named per-user synchronization primitive that serializes all mutating appliance lifecycle operations (Build, Initialize, Start, Stop, Restart, Recover, Reset) within the host core, scoped by the application data directory to ensure test isolation.

### UI Test Fixture
An isolated desktop testing harness that launches the real compiled host executable within a sandboxed temporary directory, automating UI interactions via Windows UIAutomation (FlaUI) and capturing visual screenshots and element tree dumps on failure.

### Pinned Input List
The complete list of files a Serpy release's build needs, each with a checksum, shipped with that release; the build uses exactly these files and nothing else.
*Avoid:* Lockfile, package closure

### Download Cache
The per-user store of files fetched from the Pinned Input List; a file that passed its checksum is kept there and never downloaded again.

### Tray State
One of five user-facing states shown by the tray icon and its tooltip: Setting up, Starting, Ready, Stopped, Needs attention.

### Operation Log
The durable per-operation text file that records stage changes, QEMU start and exit, every guest console line and failure detail. Show details tails it, and Save details includes it.
