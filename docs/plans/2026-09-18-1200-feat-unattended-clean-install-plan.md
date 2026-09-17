---
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-brainstorm
execution: code
---
# Unattended Clean Installation and Setup for Serpy ERPNext

## Goal Capsule
- **Objective:** Enable Serpy to run completely unattended via a dedicated CLI switch (`--unattended` / `--fresh-install`), automatically uninstalling any existing appliance instance (stopping processes, wiping instance disks, credentials, and state), building and provisioning a fresh ERPNext appliance with default credentials (`Administrator` / `admin`), verifying healthy startup, printing credentials to stdout, opening the default browser, and keeping the VM running in the system tray.
- **Means:** Implement a serialized `ResetOperation` in `Serpy.Core`, parent console attachment via Win32 API in `Serpy.App`, single-instance mutex takeover, and an unattended execution pipeline in `Program.cs` that transitions to a tray-only Avalonia host upon health verification.
- **Product Authority:** User-directed via `ce-brainstorm` session.
- **Open Blockers:** None.

## Product Contract

### Summary
Add a non-interactive `--unattended` (alias `--fresh-install`) mode to Serpy that cleans out prior appliance instances, initializes ERPNext with default credentials, boots the appliance, outputs login details to the calling terminal, opens the browser, and keeps the appliance running in the Windows tray.

### Problem Frame
Developers, testers, and automated workflows need a single command to spin up a clean Serpy ERPNext appliance from scratch without clicking through Avalonia UI dialogs or entering admin passwords. Currently, first-run initialization requires interactive GUI dialogs for credentials and setup choices, preventing headless or scripted deployment.

### Actors
- **A1: Developer / Automation Script**: Invokes `Serpy.App.exe --unattended` from PowerShell/cmd or automated CI scripts to deploy or reset a local ERPNext development environment.
- **A2: End User / Evaluator**: Runs a zero-touch setup script that automatically gets ERPNext running and opens in their browser with pre-configured default credentials ready to use.

### Requirements
- **R1: Unattended CLI Entrypoint**: Serpy must accept `--unattended` (and `--fresh-install`) command-line arguments to trigger the automated, non-interactive pipeline without showing interactive setup dialogs.
- **R2: Existing Instance Detection & Clean Uninstall**: If an existing Serpy instance or running QEMU process is detected:
  - Serpy must gracefully terminate or stop the active QEMU VM and release single-instance mutex / lifecycle locks.
  - Serpy must wipe appliance state, disks (`system.qcow2`, `data.img`, `.pending-data.img`, `.data-committed`), recovery journals, state store (`state.json`, `setup-state.json`), and DPAPI credentials (`.health-cred`).
  - Serpy must preserve the downloaded QEMU runtime binary bundle and base Debian OS image cache in `KnownPaths.RuntimeDir` and cache directories so re-installation is fast.
- **R3: Automated Build and Initialization**:
  - If `system.qcow2` is absent, run `BuildAsync` automatically using bundled cloud-init seeds and local/cached base image.
  - Run `InitializeAsync` automatically using default parameters: site name `erp.serpy.local`, admin username `Administrator`, default password `admin`.
  - Store credentials securely in the Windows DPAPI `HealthCredentials` store.
- **R4: Automated Boot & Health Verification**:
  - Automatically start the appliance via `StartAsync`.
  - Poll until appliance reaches `HealthState.Running` with loopback URL active on port 18080 (or fail with informative diagnostic error if timeout/failure occurs).
- **R5: Console Output & Credential Display**:
  - On Windows, attach to the parent console stream (`AttachConsole(-1)`) so progress updates and final status output render directly into the calling terminal.
  - Upon successful startup, print a clear summary box containing:
    - Appliance URL (e.g. `http://127.0.0.1:18080`)
    - Username (`Administrator`)
    - Password (`admin`)
- **R6: Browser Launch**:
  - Once healthy, automatically open the default system web browser to the ERPNext URL.
- **R7: Post-Setup Process Lifecycle**:
  - Serpy must not abruptly kill the QEMU VM on completion.
  - The process must transition into the background system tray (`TrayIcon`), keeping the QEMU VM running so the user can use ERPNext in their browser.

### Key Decisions
- **KD1: Dedicated CLI flag `--unattended` (with `--fresh-install` alias)**: `session-settled: user-directed`. Normal launch without flags continues to open the Avalonia GUI dashboard.
- **KD2: Preservation of runtime binaries and base image during uninstall**: `session-settled: user-directed`. Only instance-specific state, disks, and credentials are wiped; downloaded binaries and cached base image are preserved to make re-installation fast.
- **KD3: Fixed default credentials (`Administrator` / `admin`)**: `session-settled: user-directed`. Simplifies local dev/testing without requiring users to hunt for generated passwords in log files.
- **KD4: Transition to system tray after browser launch**: `session-settled: user-directed`. Keeps the QEMU VM and ERPNext server active in the background while the user works in the browser.

### Key Flows
- **F1: Fresh Unattended Run**:
  1. User/Script executes `Serpy.App.exe --unattended`.
  2. CLI attaches to parent console; emits start banner.
  3. Checks for existing instance. Finds none.
  4. Builds system image (if not already cached).
  5. Initializes data disk with default credentials (`Administrator` / `admin`).
  6. Boots appliance; verifies HTTP health check passes.
  7. Prints summary and credentials to console.
  8. Launches default web browser to ERPNext URL.
  9. Runs in system tray.
- **F2: Reinstall / Reset Flow (Existing instance present)**:
  1. User/Script executes `Serpy.App.exe --unattended`.
  2. Detects running VM / existing data disk.
  3. Stops running QEMU VM cleanly; waits for lock release.
  4. Wipes appliance data, state, and credentials.
  5. Proceeds with F1 steps 4-9.

### Scope Boundaries
- **In Scope**: CLI argument parsing, console attachment on Windows, automated uninstall/cleanup of appliance disks and state, automated build & init with default credentials, health wait, console output, browser launch, tray lifecycle.
- **Out of Scope**: Purging QEMU binary bundle (`runtime/`), multi-site Frappe configurations, remote machine installation, cloud deployment.

*Product Contract preservation: Product Contract unchanged.*

## Planning Contract

### Key Technical Decisions
- **KTD1: Add `ResetOperation` to `Serpy.Core` and `IApplianceService.ResetAsync`**: Governs R2. Instead of spreading file-deletion logic across `Serpy.App`, all disk teardown and state resetting are encapsulated in a first-class `ResetOperation` within `Serpy.Core`. This preserves the architecture invariant that all mutating appliance operations serialize through `LifecycleLock`.
- **KTD2: Selective Artifact Purge (Base Image & Runtime Preservation)**: Governs R2, KD2. `ResetOperation` explicitly targets:
  - Disks: `system.qcow2`, `data.img`, `.pending-data.img`, `.data-committed`, `seed.iso`, `recovery.journal`, `.staging-system.qcow2`.
  - State: `StateStore` (resets to `NotBuilt`), `SetupStateStore` (deletes `setup-state.json`), `HealthCredentials` (deletes `.health-cred`).
  - It explicitly PRESERVES `debian-base-*.qcow2` in `KnownPaths.ApplianceDir` and all contents of `KnownPaths.RuntimeDir`. This avoids re-downloading ~500MB of binaries on reinstall.
- **KTD3: Native-AOT Compatible Console Attachment (`[LibraryImport]`)**: Governs R5. Windows `WinExe` applications are detached from the console by default. `Serpy.App` will use `[LibraryImport("kernel32.dll", SetLastError = true)] static partial bool AttachConsole(int dwProcessId);` with `ATTACH_PARENT_PROCESS = -1`, followed by `Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true })`. This is completely Native-AOT compatible and requires no reflection.
- **KTD4: Pre-Run Mutex & Process Takeover in `--unattended`**: Governs R2. When `--unattended` is passed and `singleInstanceMutex` cannot be acquired, `Serpy.App` detects running `Serpy.App` processes for the current user, requests clean shutdown, waits up to 5 seconds, forces termination if necessary, and acquires the mutex to proceed cleanly with the reset.
- **KTD5: Pipeline Execution with Transition to Tray-Only Desktop Lifetime**: Governs R6, R7, KD4. The unattended pipeline runs sequentially in `Program.cs` before starting the Avalonia desktop lifetime. Once health is confirmed and the browser is launched, `Program.cs` invokes `BuildAvaloniaApp(trayOnly: true, service).StartWithClassicDesktopLifetime(args)` so Serpy continues to host the QEMU appliance in the background tray.

### High-Level Technical Design

```mermaid
sequenceDiagram
    autonumber
    actor Caller as Terminal / Script
    participant Prog as Serpy.App (Program.cs)
    participant Console as Windows Console
    participant Core as ApplianceService (Serpy.Core)
    participant VM as QEMU / Guest
    participant Browser as Default Web Browser
    participant Tray as Avalonia TrayHost

    Caller->>Prog: Serpy.App.exe --unattended
    Prog->>Console: AttachConsole(-1) & Redirect Stdout
    Prog->>Prog: Check SingleInstance Mutex
    alt Mutex held by existing Serpy
        Prog->>Prog: Signal / Terminate prior process & acquire mutex
    end
    Prog->>Core: GetStatusAsync()
    alt Appliance exists or running
        Prog->>Core: ResetAsync()
        Core->>VM: Stop QEMU (ACPI / Kill)
        Core->>Core: Wipe disks & state (preserve base image & runtime)
    end
    Prog->>Core: BuildAsync() [if system.qcow2 absent]
    Core->>Core: Cloud-init provision system.qcow2
    Prog->>Core: InitializeAsync(defaultParams)
    Core->>Core: Format data.img, seed MariaDB & ERPNext (Administrator/admin)
    Prog->>Core: StartAsync()
    Core->>VM: Boot QEMU with WHPX & port forwarding (18080)
    VM-->>Core: HTTP Health Check passes (port 18080)
    Prog->>Console: Print Credentials Box (URL, Username, Password)
    Prog->>Browser: Open http://127.0.0.1:18080
    Prog->>Tray: StartClassicDesktopLifetime(trayOnly: true)
    Note over Tray,VM: Serpy stays alive in system tray; VM remains running
```

### Assumptions
- The execution host is Windows x64 with WHPX enabled (or supported acceleration fallback).
- Port 18080 is available for slirp loopback port forwarding (standard Serpy default).
- The default administrator credentials `Administrator` / `admin` are acceptable for local non-interactive developer instances.

### Dependencies & Integrations
- `kernel32.dll` (`AttachConsole`, Windows only) for parent terminal attachment.
- `System.Threading.Mutex` for single-instance orchestration.
- `Serpy.Core.Contracts.IApplianceService` and `LifecycleLock` for serialized mutation.
- `Serpy.App.Platform.BrowserLauncher` for launching the user's default browser.

---

## Implementation Units

### U1: Core Reset Operation & Appliance Service Teardown
- **Target:**
  - `src/Serpy.Core/Contracts/OperationKind.cs`
  - `src/Serpy.Core/Contracts/IApplianceService.cs`
  - `src/Serpy.Core/Operations/ResetOperation.cs` (new)
  - `src/Serpy.Core/Operations/ApplianceService.cs`
  - `tests/Serpy.App.Tests/FakeApplianceService.cs`
- **Approach:**
  1. Add `Reset` to `OperationKind` enum in `src/Serpy.Core/Contracts/ApplianceStatus.cs`.
  2. Add `Task<OperationResult> ResetAsync(IProgress<OperationUpdate> progress, CancellationToken ct = default);` to `IApplianceService`.
  3. Implement `ResetOperation.cs`:
     - If QEMU PID exists in `StateStore` and is alive, call `StopOperation` (or kill process tree if unresponsive).
     - Remove appliance disks and markers: `system.qcow2`, `.staging-system.qcow2`, `data.img`, `.pending-data.img`, `.data-committed`, `recovery.journal`, `seed.iso`.
     - Explicitly preserve `debian-base-*.qcow2` in `KnownPaths.ApplianceDir` and the entire `KnownPaths.RuntimeDir`.
     - Delete stored health credentials via `HealthCredentials.Delete()`.
     - Reset `StateStore` to `new ApplianceState { Readiness = ReadinessState.NotBuilt, Health = HealthState.Stopped }`.
     - Delete `setup-state.json` if present.
     - If on Windows, call `RunKeyAutostart.Disable()`.
  4. Wire `_resetOp` in `ApplianceService.cs` with `_lock.TryAcquire(TimeSpan.Zero)` to serialize through the lifecycle lock.
  5. Implement `ResetAsync` in `FakeApplianceService.cs` for unit test compatibility.
- **Patterns to follow:** `StopOperation.cs`, `BuildOperation.cs`, `InitializeOperation.cs`.
- **Test scenarios:**
  - `ResetAsync_WhenApplianceRunning_StopsQemuAndWipesDisks`: Verify running QEMU is halted, `system.qcow2` and `data.img` are deleted, and `StateStore.Read().Readiness` is `NotBuilt`.
  - `ResetAsync_PreservesBaseImageAndRuntime`: Verify `debian-base-*.qcow2` and `runtime/` directory remain untouched on disk after reset.
  - `ResetAsync_WhenEmptyDirectory_SucceedsIdempotently`: Verify calling reset on a clean system succeeds without error.

### U2: Windows Console Attachment Helper (AOT-Compatible)
- **Target:**
  - `src/Serpy.App/Platform/Windows/ConsoleAttachment.cs` (new)
- **Approach:**
  1. Define `ConsoleAttachment` internal static class guarded by `OperatingSystem.IsWindows()`.
  2. Implement `[LibraryImport("kernel32.dll", SetLastError = true)] private static partial bool AttachConsole(int dwProcessId);` with `const int ATTACH_PARENT_PROCESS = -1;`.
  3. Provide `public static bool TryAttachParent()`:
     - Calls `AttachConsole(ATTACH_PARENT_PROCESS)`.
     - If true, re-opens standard output and standard error via `new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }` and calls `Console.SetOut(...)` and `Console.SetError(...)`.
     - Writes an initial blank line so output starts cleanly after the prompt.
- **Patterns to follow:** `RunKeyAutostart.cs` (using `[SupportedOSPlatform("windows")]` and AOT-safe APIs).
- **Test scenarios:**
  - `TryAttachParent_DoesNotThrowOnNonWindows`: Verify calling on non-Windows returns false cleanly without crashing.
  - Native-AOT compilation smoke: verify `Serpy.App` compiles without trim/AOT warnings.

### U3: Process Pre-flight & Mutex Takeover
- **Target:**
  - `src/Serpy.App/Platform/ProcessController.cs` (new or in `Program.cs`)
- **Approach:**
  1. Implement helper method `EnsureSoleInstanceForUnattended(string mutexName)`:
     - Attempt to acquire the mutex with a 1-second timeout.
     - If acquisition fails (another instance is running):
       - Log to console: `Existing Serpy process detected. Requesting shutdown…`.
       - Enumerate processes named `Serpy.App` (excluding current process ID).
       - Request graceful close via `CloseMainWindow()`; wait 3 seconds.
       - If still running, call `Kill(entireProcessTree: true)`.
       - Wait up to 5 seconds to acquire the mutex.
       - If acquisition still fails, throw informative exception.
- **Patterns to follow:** `StopOperation.cs` process termination logic.
- **Test scenarios:**
  - Verify helper identifies foreign PIDs and handles graceful/kill timeouts without crashing.

### U4: Unattended Execution Pipeline in `Program.cs`
- **Target:**
  - `src/Serpy.App/Program.cs`
- **Approach:**
  1. Detect unattended flags:
     `bool isUnattended = args.Contains("--unattended", StringComparer.OrdinalIgnoreCase) || args.Contains("--fresh-install", StringComparer.OrdinalIgnoreCase);`
  2. If `isUnattended`:
     - Call `ConsoleAttachment.TryAttachParent()`.
     - Print header:
       ```
       ==================================================
       Serpy ERPNext Unattended Setup
       ==================================================
       ```
     - Run `EnsureSoleInstanceForUnattended(...)`.
     - Build composition root (`ApplianceService`, etc.).
     - Check status: if `HasCommittedDataOnDisk()` or `status.Readiness != NotBuilt`:
       - Console write: `Existing installation detected. Uninstalling…`.
       - `await service.ResetAsync(progress, ct);`
       - Console write: `Prior installation cleaned.`.
     - Build system image if needed:
       - Console write: `Building system image (this may take several minutes on first run)…`.
       - `await service.BuildAsync(progress, ct);`.
     - Initialize ERPNext:
       - Console write: `Initializing ERPNext data disk with default credentials…`.
       - `var initParams = new InitializationParameters("erp.serpy.local", "admin");`
       - `await service.InitializeAsync(initParams, progress, ct);`.
     - Start appliance:
       - Console write: `Starting appliance and verifying health…`.
       - `await service.StartAsync(progress, ct);`.
     - Wait for `HealthState.Running` (up to 120s):
       - Print success credentials box:
         ```
         ==================================================
         Serpy ERPNext Installation Complete!
         ==================================================
         URL:      http://127.0.0.1:18080
         Username: Administrator
         Password: admin
         ==================================================
         ```
       - Open browser: `new BrowserLauncher().OpenOnce(loopbackUrl);`.
       - Console write: `Opening default browser. Serpy will now run in the background tray.`.
     - Launch Avalonia in tray-only mode:
       `return BuildAvaloniaApp(trayOnly: true, service).StartWithClassicDesktopLifetime(args);`.
  3. If not unattended, continue existing `Program.cs` GUI startup flow.
- **Patterns to follow:** Existing `Program.cs` composition root and `DashboardViewModel.cs` dispatch logic.
- **Test scenarios:**
  - Verify CLI argument detection for `--unattended` and `--fresh-install`.
  - Verify that standard launch without arguments continues to show the interactive GUI dashboard window.

### U5: Verification & Automated Regression Tests
- **Target:**
  - `tests/Serpy.Core.Tests/Operations/ResetOperationTests.cs` (new)
  - `tests/Serpy.App.Tests/UnattendedArgsTests.cs` (new)
- **Approach:**
  1. Add `ResetOperationTests.cs`:
     - Test disk deletion and state reset to `NotBuilt`.
     - Test preservation of base image `debian-base-*.qcow2`.
     - Test idempotency when no files exist.
  2. Add `UnattendedArgsTests.cs`:
     - Test `--unattended` and `--fresh-install` flags recognition.
  3. Run `dotnet build Serpy.slnx -c Release`.
  4. Run `dotnet test Serpy.slnx -c Release --filter "FullyQualifiedName!~IntegrationTests"`.
- **Patterns to follow:** `tests/Serpy.Core.Tests/Operations/ApplianceServiceTests.cs`.
- **Test scenarios:**
  - Full test suite passes without regressions.
  - Zero compiler warnings or errors (`TreatWarningsAsErrors` enabled).

---

## Verification Contract

### Automated Verification
- **Build verification:**
  ```bash
  dotnet build Serpy.slnx -c Release
  ```
  Must compile with zero warnings and zero errors across all projects (`TreatWarningsAsErrors`).
- **Core test suite:**
  ```bash
  dotnet test Serpy.slnx -c Release --filter "FullyQualifiedName!~IntegrationTests"
  ```
  All unit tests in `Serpy.Core.Tests` and `Serpy.App.Tests` must pass.

### Manual / Scenario Verification
1. **Clean Unattended Run Scenario:**
   - Run from PowerShell: `.\src\Serpy.App\bin\Release\net10.0\win-x64\Serpy.App.exe --unattended`
   - Observe progress output in PowerShell console.
   - Observe credentials box printed with URL, Username (`Administrator`), and Password (`admin`).
   - Observe default browser opening to `http://127.0.0.1:18080`.
   - Observe Serpy tray icon appearing in Windows notification area.
2. **Re-run (Uninstall Existing) Scenario:**
   - With the previous instance running or existing data on disk, run: `Serpy.App.exe --unattended`
   - Observe detection of prior instance: existing VM stopped, old data deleted.
   - Observe fresh build/init/start completing successfully.
   - Verify browser opens to clean fresh instance.

---

## Definition of Done
- [ ] `ResetOperation` implemented and integrated into `IApplianceService` / `ApplianceService`.
- [ ] Selective wipe implemented: appliance disks and state are wiped; `debian-base-*.qcow2` and `KnownPaths.RuntimeDir` are preserved.
- [ ] Windows console attachment implemented using Native-AOT compatible P/Invoke.
- [ ] Single-instance mutex takeover and process shutdown implemented for unattended runs.
- [ ] `--unattended` and `--fresh-install` CLI arguments parsed and sequence executed in `Program.cs`.
- [ ] Default credentials (`Administrator` / `admin`) initialized and displayed on stdout.
- [ ] Browser automatically launched upon successful health check.
- [ ] Process transitions to Avalonia system tray so appliance remains alive for browser usage.
- [ ] All unit tests pass in `Release` configuration with zero compiler warnings.
