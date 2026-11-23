---
title: Fix clean-install defects
type: fix
status: active
date: 2026-11-22
---

# Fix clean-install defects

## Problem frame

A real clean-install test on Windows VMs found three defects in the first-run experience:

1. Windows 10 Enterprise LTSC 2021 (build 19044) is rejected: the PC check demanded build 19045
   (22H2), which 19044 can never reach, so a supported OS failed with "needed: build 19045 or
   later".
2. A genuine 8 GB PC failed the memory check: Windows reports usable RAM slightly under 8 GiB
   (hardware/firmware reserve) and the rule was a flat `>= 8 GiB`; the log also read
   "Memory Fail measured: 8 GB; needed: 8 GB" because the formatter rounded 7.5–7.9 up to "8".
3. When the PC check failed, the CannotRun screen said "fix what is listed above" but nothing was
   listed: the PC check report travelled only as log text (`SetupContext.PcCheckReport`),
   `SetupUpdate` carried no findings, and the screen had no list to show. There was no orphaned
   collection to rewire — the structured lines never reached the UI at all.

## Requirements

- R-A: Build 19044 passes the Windows version check; 19043 and older fail. Failing text states the
  needed Windows version in plain words ("Windows 10 or 11"), not only a build number.
- R-B: A real 8 GB PC passes memory. The product requirement stays "8 GB"; the comparison uses a
  usable-memory floor with the tolerance documented on the threshold record. Fail lines never show
  two equal-looking figures; figures carry one decimal.
- R-C: The CannotRun failure screen lists every failed and needs-attention PC check line in plain
  language, without opening "Show details". Core carries the lines; the ViewModel only maps them;
  XAML binds exactly the way existing compiled bindings do (Native-AOT safe). ViewModels gain no
  lifecycle logic.

## Key decisions (with rationale)

- KD-A1: `MinWindowsBuild` = 19044 (Windows 10 21H2, incl. Enterprise LTSC 2021). WHPX ships in
  every Windows 10 2004 (19041) and later edition, so 19044 can always run the appliance.
- KD-A2: The Windows line says what was found and needed in words: measured "Windows 10 (build
  19044)" / "Windows 11 (build 26200)" / "an older version of Windows"; needed "Windows 10 or 11
  (build 19044 or newer)". The name is the message for a non-technical user; the build stays for
  the person who helps them.
- KD-B1: `MinMemoryBytes` is a 7 GiB usable-memory floor standing in for the 8 GB product
  requirement. `GlobalMemoryStatusEx.TotalPhys` (what the probe reads) reports memory below the
  installed size by the firmware/hardware reserve; a genuine 8 GB PC reads ~7.7–7.9 GiB usable.
  The floor tolerates that reserve and still fails 6 GB-class machines. Documented in
  `<remarks>` on `PcThresholds` so the next threshold change sees why.
- KD-B2: One decimal everywhere in the PC check's GB formatter, and "usable" on memory lines, so
  a failing line can never display two equal-looking figures ("6.0 GB usable; needed: 7.0 GB
  usable"; before: "8 GB; needed: 8 GB"). Disk keeps the same rule ("29.9 GB free" vs "30.0 GB").
  "GB" names 1024-based bytes throughout, consistent with the existing code.
- KD-C1: `SetupUpdate` gains an optional `IReadOnlyList<PcCheckLine>? CheckFindings`. The PC check
  step sets `report.Findings` (fail + needs-prepare lines) on `SetupContext` next to the existing
  text report; `SetupOperation` captures them under the same `_gate` as the report and includes
  them in every emit — the same mechanism `_downloadParts` already uses, not a second channel.
- KD-C2: The ViewModel exposes `ObservableCollection<string> CheckFindings` composed through
  `UiText.CheckFinding` (label + value + needed, the same plain-word mapping the check list
  already uses; virtualization/internet lines keep their paraphrased wording per R40), filled on
  failed/cancelled updates and cleared in the existing per-run reset (`StartDetailsTail`). The
  CannotRun screen shows it with an `ItemsControl` bound like `WelcomeFacts` — the shipped
  AOT-safe pattern. Only CannotRun binds it, because that is the screen whose "what to do" text
  refers to the list.
- KD-C3: Historical documents (the 2026-11-20 plan's KTD13, mockups) are dated records and keep
  their original figures; README.md (user-facing prerequisite) is corrected. eng/installer is
  owned by the MSI worker; `Serpy.wxs` has no Windows-version launch condition, so it needs none.

## Implementation units

- U1 — Windows gate (KD-A1, KD-A2) — DONE. `src/Serpy.Core/Setup/PcCheck.cs` (threshold +
  `WindowsName` wording); `README.md` prerequisite line. Tests in
  `tests/Serpy.Core.Tests/Setup/PcCheckTests.cs`: `AWindows10Ltsc2021Build_Passes_WithPlainVersionWord`
  (19044 passes, says "Windows 10"); `AWindowsBuildBelowTheThreshold_Fails_NamingTheWindowsNeeded`
  (19043 fails, needed text says "Windows 10 or 11 … 19044"); `AWindows11Build_NamesWindows11`.
- U2 — Memory rule and figures (KD-B1, KD-B2) — DONE. `src/Serpy.Core/Setup/PcCheck.cs`. Tests:
  `AnEightGbPc_Passes_...` (7.75 GiB usable passes, shows "7.8 GB usable");
  `SixGb_Fails_WithFiguresThatCannotLookEqual`; `MemoryBelowTheThreshold_Fails_AndReportsDistinctFigures`
  (4 GiB fails); `JustUnderTheDiskThreshold_ShowsTwoDistinctFigures` (29.9 vs 30.0);
  `TheReportText_NamesEachCheckWithItsMeasuredAndNeededValues` (one decimal, "usable").
- U3 — Findings to the failure screen (KD-C1..C3) — DONE. `src/Serpy.Core/Setup/PcCheck.cs`
  (`PcCheckReport.Findings`), `src/Serpy.Core/Setup/ISetupStep.cs` (`SetupContext.PcCheckFindings`),
  `src/Serpy.Core/Setup/PcCheckStep.cs`, `src/Serpy.Core/Contracts/SetupContracts.cs`
  (`SetupUpdate.CheckFindings`), `src/Serpy.Core/Operations/SetupOperation.cs` (capture + emit),
  `src/Serpy.App/Text/UiText.cs` (`CheckFinding`), `src/Serpy.App/ViewModels/SetupViewModel.cs`
  (`CheckFindings` collection), `src/Serpy.App/Views/SetupView.axaml` (`CannotRunFindingsList`).
  Tests: `PcCheckTests.Findings_ListTheFailedAndNeedsPrepareLines_InReportOrder` +
  `Findings_IsEmpty_WhenEveryCheckPasses`; `SetupViewModelTests.FailedPcCheck_ExposesFindingLinesForTheCannotRunScreen`
  + `AFailureWithoutFindings_ListsNothingRatherThanStaleLines`; `UiTextTests.AFailureLine_ComposesLabelFoundAndNeeded_InPlainWords`;
  `LayoutInvariantTests.SetupView_CannotRunScreen_ListsThePcCheckFindingsWithoutShowDetails`
  (headless: the list renders on the CannotRun screen).

## Risks

- The PC check text is echoed by the details file, the log and the step failure message; the
  figure/wording changes required re-pinning those assertions. Done without weakening intent
  (figures and version words are still asserted).
- `SetupUpdate` grew a field; the parameter is optional with named-argument use at the new call
  sites, so existing positional constructions kept compiling.
- The `ItemsControl` of plain strings reuses the shipped `WelcomeFacts` binding pattern (no
  reflection); a headless layout test proves the list actually renders, guarding against silent
  binding failures under Native-AOT.
- The Windows build→name mapping reads non-client builds as "an older version of"; acceptable for
  a client-OS product, and the build number stays visible for support.

## Download body stall timeout (defect 4)

### Problem
During the Windows 10 LTSC clean-install VM run, Prepare passed but the Download step stalled
indefinitely with 4 `.part` files partially written (cryptography, duckdb, holidays, ipython wheels
from `files.pythonhosted.org`). No bytes were written for 8+ minutes, with only the watchdog line
`No activity for 2 minutes; the Download step may be stuck.`.
Root cause: `DownloadEngine.StreamToFileAsync` requests files with
`HttpCompletionOption.ResponseHeadersRead`, meaning `HttpClient.Timeout` (100 s) only governs the
header phase. The body read loop `stream.ReadAsync(buffer, ct)` had no inactivity timeout; a stalled
remote TCP connection blocked forever, holding throttle slots and preventing retry, backoff, and URL
fallback.

### Decision (KD-D1)
- Add `DefaultStallTimeout` (60 seconds, `DefaultStallTimeoutSeconds = 60`) on `DownloadEngine`
  with an optional `TimeSpan? stallTimeout` constructor parameter for deterministic test injection.
- In `StreamToFileAsync`, wrap each `stream.ReadAsync` call in a linked `CancellationTokenSource`
  with `readCts.CancelAfter(_stallTimeout)`. If it fires when the outer `ct` is not cancelled,
  throw `TimeoutException($"No data received for {_stallTimeout.TotalSeconds}s from {url}")`.
- The existing retry logic in `DownloadFromUrlWithRetryAsync` catches `TimeoutException`, deletes the
  partial `.part` file, waits with exponential backoff up to 3 retries, falls back to alternative URLs,
  and reports `FailureCode.NetworkUnavailable` if all addresses fail.
- `IsNetworkException` includes `TimeoutException`, and retry/fallback guards check
  `!ct.IsCancellationRequested` to ensure clean cancellation is not caught as a network fault.

### Tests
- `StalledBodyRead_TimesOutAndRetriesSuccessfully`: stream stalls after initial bytes; 150 ms stall
  timeout triggers retry, and attempt 2 succeeds with verified file and cleaned-up `.part`.
- `AllAttemptsStall_ThrowsNetworkUnavailableWithoutHanging`: all attempts stall; engine throws
  `DownloadException(FailureCode.NetworkUnavailable)` within a few seconds via safety timeout.
- `SlowButProgressingStream_DoesNotTriggerStallTimeout`: chunks arrive every 50 ms (total duration
  200 ms > 100 ms stall timeout); stream completes successfully because no individual read stalls.

## PC check Internet probe transient retry (defect 5)

### Problem
During the Windows 11 clean-install VM regression run, the PC check failed on the Internet check with:
`14:50:44 Internet Fail measured: no answer from archive.mariadb.org; needed: every download site`
exactly 8.0 s after PcCheck started (14:50:36), right after the VM's first boot. In the same guest
immediately afterwards, all 9 hosts responded within ~1 s (archive.mariadb.org in 0.72 s).
Root cause: `WindowsPcProbe.ReachAsync` sent a single HEAD request per host with an 8.0 s
`HttpClient.Timeout`. In Hyper-V VM environments (e.g. Default Switch NAT), guest hosts may publish
both IPv4 (A) and IPv6 (AAAA) DNS records while the NAT interface lacks an external IPv6 route. The
initial connection attempt to dual-stack mirrors such as archive.mariadb.org can incur an OS-level
connect/fallback delay before Windows falls back to IPv4. A single transient timeout caused the entire
PC check to fail closed with `NetworkUnavailable`.

### Decision (KD-E1)
- For each host in `WindowsPcProbe.ReachAsync`, retry up to 2 additional times (3 attempts total) with
  escalating per-attempt timeouts: 8 s, 12 s, 15 s (`DefaultAttemptTimeouts`), with a 1 s delay
  (`DefaultRetryDelay`) between attempts.
- Timeouts are controlled per-attempt using a linked `CancellationTokenSource` with `CancelAfter`,
  while `HttpClient.Timeout` is set to `InfiniteTimeSpan`.
- Hosts are still probed in parallel across distinct hostnames; overall wall time on success remains
  ~1 s, and worst case for a completely dead host is ~37 s.
- Any HTTP response (including 403, 404, 405, 5xx) counts as reachable.
- User cancellation via the outer `CancellationToken` immediately aborts without waiting and throws
  `OperationCanceledException` (not reported as unreachable).
- Unsafe socket hacks (e.g. `ConnectCallback` forcing IPv4 first) are avoided; retry with escalating
  timeouts absorbs the OS connect delay cleanly.
- Injectable `HttpMessageHandler`, `attemptTimeouts`, `retryDelay`, and `delay` function parameters on
  `WindowsPcProbe` allow deterministic, fast unit testing.

### Tests
- `ReachAsync_FirstAttemptTimesOut_SecondAttemptSucceeds_HostReachable`: first attempt times out;
  probe retries and second attempt succeeds, reporting reachable.
- `ReachAsync_AllAttemptsFail_HostUnreachable_FinishesQuickly`: all attempts fail; probe reports
  unreachable and completes quickly under test timeouts.
- `ReachAsync_Http404_CountsAsReachable`: 404 response counts as reachable.
- `ReachAsync_Cancellation_ThrowsOperationCanceledException_NotUnreachable`: cancelling caller token
  throws `OperationCanceledException` rather than returning an unreachable result.

## Download body premature-end retry (defect 6)

### Problem
During the Windows 11 clean-install VM run, setup failed at Download with 1,372 of 1,375 files complete:
`15:48:12 DownloadStep failed: The response ended prematurely, with at least 34456 additional bytes expected. (ResponseEnded)` -> `lastFailure ProvisioningFailed`.
The remote server cut the connection near the end of a 22 MB wheel (`uv-0.11.33`).
Root cause: `stream.ReadAsync` threw `HttpIOException` (`HttpRequestError.ResponseEnded`, a subclass of
`System.IO.IOException`). `DownloadFromUrlWithRetryAsync` only retried `TaskCanceledException`,
`TimeoutException`, and `HttpRequestException`, while `IsNetworkException` excluded `IOException`.
Consequently, neither the 3-attempt retry loop nor URL fallback ran; the unhandled `IOException` escaped
`DownloadEngine` directly into `DownloadStep`'s generic exception handler, failing the entire install
with `ProvisioningFailed`.

### Decision (KD-F1)
- In `DownloadEngine.StreamToFileAsync`, wrap network stream operations (`response.Content.ReadAsStreamAsync`
  and `stream.ReadAsync`) to catch any non-disk-full `IOException` and rethrow it as
  `HttpRequestException($"Network stream read error from {url}: {ex.Message}", ex)`.
- File write operations (`fileStream.WriteAsync` and `new FileStream(...)`) are deliberately excluded
  from this wrapping; any disk-full `IOException` (`IsDiskFullException(ex)`) bypasses network retry,
  cleans up the partial `.part` file, terminates URL fallback, and maps directly to
  `DownloadException(FailureCode.DiskFull, ...)`.
- Re-thrown `HttpRequestException` engages the existing `maxRetries = 3` retry with exponential backoff
  on fresh `FileMode.Create` file streams, followed by alternate URL fallback, and reports
  `FailureCode.NetworkUnavailable` if all addresses are exhausted.

### Tests
- `PrematureResponseEnd_RetriesAndSucceeds`: stream throws `IOException` ("response ended prematurely")
  on attempt 1, attempt 2 succeeds; full file verified and partial `.part` file cleaned up.
- `PrematureResponseEnd_AllAttemptsFail_ThrowsNetworkUnavailable`: all attempts throw `IOException`;
  engine fails closed with `DownloadException(FailureCode.NetworkUnavailable)` rather than escaping as raw
  `IOException` / `ProvisioningFailed`.

## Download resume within a run (defect 7)

### Observation
In VM clean-install runs on Windows 11, slow upstream mirrors (such as `archive.mariadb.org`)
throttle to ~15 KB/s and cut connections after ~4 MB. Because files such as
`mariadb-server-core_11.8.3+maria~deb13_amd64.deb` (7,856,336 bytes) have only a single URL,
retrying from offset 0 repeatedly stalls and fails with `NetworkUnavailable` ("The response
ended prematurely"). The upstream mirror supports standard HTTP Range requests (`Range: bytes=4000000-`
returns `206 Partial Content` with `Content-Range: bytes 4000000-7856335/7856336`).

### Decision (KD-F2)
- **Same-URL Resume**: In `DownloadEngine.StreamToFileAsync`, when retrying on the same URL after
  a transient network error (`HttpRequestException`, `TimeoutException`, `TaskCanceledException`),
  the engine preserves the `.part` file on disk and issues an HTTP Range request starting at
  `new FileInfo(partPath).Length`.
- **Range Validation & Fallbacks**:
  - If the server answers `206 Partial Content`, the engine verifies that `Content-Range` starts
    at exactly the requested offset (and matches `file.Size` if known). If valid, the stream appends
    via `FileMode.Append`. If invalid, `.part` is deleted, negative progress is reported, and an
    `HttpRequestException` is thrown to restart fresh from 0.
  - If the server ignores `Range` and answers `200 OK`, the engine resets progress via
    `onBytesReceived(-resumeFrom)`, truncates the `.part` file (`FileMode.Create`), and restarts from 0.
  - If the server answers `416 Range Not Satisfiable`, the engine deletes `.part`, resets progress,
    and restarts fresh from 0.
- **Forward Progress Accounting**:
  - Retries that achieve forward progress (`partLengthNow > bytesBefore`) do not consume the retry budget
    (`noProgressCount`). An overall safety cap of `MaxResumedAttemptsPerFile = 50` prevents infinite loops.
  - Zero-progress failures consume the `maxRetries = 3` budget.
- **Part Isolation & Clean Start**:
  - Pre-existing `.part` files are deleted at entry to `DownloadSingleFileAsync`.
  - Switching URLs on fallback or refetching on checksum mismatch deletes the `.part` file and resets
    progress to avoid corrupting byte streams across mirrors.

### Tests
- `ResumesPartialDownload_WhenConnectionClosesMidStream`: Connection cut after 40 bytes; second request
  sends `Range: bytes=40-`, receives 206 Partial Content, appends remaining 60 bytes; download succeeds;
  progress snapshots never exceed file size.
- `ServerIgnoresRange_RestartsFromZeroCleanly`: Server ignores Range header and serves full 200 OK;
  file truncates and restarts from 0 with exact progress accounting.
- `ServerReturnsUnacceptableContentRange_RestartsFromZeroAndSucceeds`: Server returns invalid Content-Range;
  engine deletes `.part` and successfully restarts from 0 on the next attempt.
- `CutAfterKBytes_CompletesViaProgressiveResumesWithoutBudgetExhaustion`: 20-byte file cut every 5 bytes;
  resumes 4 times without exhausting retry budget, verifying forward progress policy.
- `ZeroProgressFailures_ExhaustRetryBudget`: Zero-progress failures exhaust retry budget after exactly
  4 attempts (initial + 3 retries) and fail with `FailureCode.NetworkUnavailable`.
- `FallbackToNextUrl_CleansUpPartFileAndStartsWithoutRange`: Fallback to secondary URL deletes `.part`
  file and begins without Range header.

## MSI .NET runtime (owned by msi worker)

### Decision (KD_MSI1) — Harvested runtime files, no nested installer

The Serpy MSI (WiX v5, per-machine, one-click product stays an MSI — no bundle)
harvests the official Microsoft `dotnet-runtime-10.0.15-win-x64.zip` files
directly into the MSI under `[ProgramFiles64Folder]dotnet\` as a conditional,
PERMANENT Feature (`DotNetRuntimeFeature`, Level 0 by default, raised to Level 1
ONLY when `NOT DOTNETCOREAPPPRESENT`).

**Why the custom-action / embedded-bundle approach failed (VM-verified):**
`dotnet-runtime-10.0.15-win-x64.exe` is a Burn bundle that chains its own
msiexec. While `Serpy.msi` is executing, the Windows Installer service's
execution mutex is held by `Serpy.msi`. The nested runtime MSI repeatedly logs:

    Failed to grab execution mutex. System error 258 ... MainEngineThread is returning 1618

The child bundle exits with `0x652` (`1618` = `ERROR_INSTALL_ALREADY_RUNNING`),
WixQuietExec64 fails the deferred custom action, and `Serpy.msi` rolls back
with 1603. Running any Burn bundle or nested MSI from inside an MSI execution
sequence is fundamentally blocked by Windows Installer's single-engine mutex.

**New architecture:**
- **Zero execution at install time:** no embedded `.exe`, no deferred CA, no
  type-51 SetProperty, no cmd.exe wrapper, no exit-code gates, no Error 16001.
- **Harvested payload:** `build-msi.ps1` downloads the official ZIP (single
  pin), verifies its published SHA-512, extracts it to `.tmp-vm/dotnet-runtime-zip/extracted/`,
  and generates a companion WiX fragment (`harvest.wxs`) that creates an
  explicit, 64-bit, permanent component for every file (193 files + 1 registry
  component).
- **Feature & Condition:** `DotNetRuntimeFeature` has `Level="0"` (absent by
  default) and a `<Level Value="1" Condition="NOT DOTNETCOREAPPPRESENT" />`
  child. On machines with any .NET runtime present, the feature stays Level 0
  and nothing is touched; on a clean machine, the feature elevates to Level 1
  and Windows Installer copies the files atomically.
- **Target directory:** `[ProgramFiles64Folder]dotnet\` (`C:\Program Files\dotnet\`).
  This is the first machine-wide directory hostfxr searches by default, so
  `Serpy.InstallerBootstrapper.exe` (framework-dependent) resolves it with
  zero configuration or environment variables.
- **Permanent components:** all 194 components (193 files + registry) have
  attribute bit `0x10` (`msidbComponentAttributesPermanent`). Uninstalling
  Serpy removes Serpy's files under `%ProgramFiles%\Serpy` and leaves
  `C:\Program Files\dotnet\` intact — matching the shared machine-wide
  contract other .NET applications expect.
- **Registry values:** the harvest fragment writes:
  - `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64` `InstallLocation` = `[DotNetRuntimeFolder]` (`C:\Program Files\dotnet\`)
  - `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App\10.0.15` = `1` (DWORD)
  These match what Microsoft's own installer writes, so detectors (including
  future Serpy runs) see the runtime installed and `DOTNETCOREAPPPRESENT`
  evaluates truthfully.
- **Shared-file / conflict semantics:** if a file already exists from another
  installer (e.g. an SDK was installed), standard Windows Installer file
  versioning rules decide: a harvested file with equal or lower version never
  overwrites a higher-versioned file on disk. A newer runtime installed by
  the user always wins.

**Pin (single source of truth):** `eng/installer/build-msi.ps1` parameter
defaults —
- `DotNetRuntimeVersion = "10.0.15"`
- `DotNetRuntimeZipSha512 = "06f0bedd59ae4ac722ada805a6d6bbf605bcdc790c71f59b7003878072902b5cde5dec8c40c0baa81192660d4748e9d79afdbdcb0c0ea9d6b821d0a044c4c6dd"`
Verified at BUILD time against Microsoft's published checksums file
(`https://builds.dotnet.microsoft.com/dotnet/checksums/10.0.15-sha.txt`).

**Detection:** `AppSearch` → `RegistrySearch` (Type=raw, Win64 context via
`-arch x64`) on `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App`
(key existence). Property `DOTNETCOREAPPPRESENT`.

### Files
- `eng/installer/build-msi.ps1` — zip pin defaults, build-time fetch/cache
  (`.tmp-vm/dotnet-runtime-zip/`), SHA-512 verification, extraction to staging,
  automatic `harvest.wxs` companion generator (194 permanent 64-bit components),
  `-DotNetRuntimeZipPath` parameter, `-AllowMissingDotNetRuntime` dev escape hatch.
- `eng/installer/Serpy.wxs` — `DOTNETCOREAPPPRESENT` RegLocator,
  `DOTNETRUNTIMEPAYLOAD` hidden marker property, `DotNetRuntimeFeature`
  (Level 0, elevated to Level 1 when `NOT DOTNETCOREAPPPRESENT`), referencing
  the harvested `DotNetRuntimeFiles` component group; all CA code removed.
- `.github/workflows/build-test-publish.yml` — downloads the pinned ZIP into
  `dist/dotnet-runtime/` and passes `-DotNetRuntimeZipPath` to `build-msi.ps1`.
- `README.md` "Prerequisites (Windows)" — updated: MSI includes the runtime
  files directly and deploys them when Windows has none.

### Test scenarios (VM session, clean snapshot)
1. Clean Windows, no .NET: install MSI (pass `/l*v` log). Expect: no manual
   step; `DotNetRuntimeFeature` is selected (Level 1); files land in
   `C:\Program Files\dotnet\`; registry keys written; helper runs at first
   setup (zero timeouts).
2. Install on machine with runtime present (`DOTNETCOREAPPPRESENT` true):
   log shows `DotNetRuntimeFeature` absent (Level 0); no files written to
   `C:\Program Files\dotnet\`.
3. Uninstall Serpy: `%ProgramFiles%\Serpy` removed; `C:\Program Files\dotnet\`
   and the registry keys remain intact (`Permanent="yes"` components).
4. Dev escape hatch: `build-msi.ps1 -AllowMissingDotNetRuntime` builds a
   valid ~60 MB MSI without the runtime feature.

### Risks
- MSI size grows ~30 MB (embedded compressed cab for 193 files; MSI is ~91 MB)
  — accepted for true offline one-click install without nested mutex bugs.
- Permanent components leave ~77 MB on disk after Serpy uninstall — documented,
  intended behavior for shared machine-wide runtimes.
- Microsoft republishes the zip with different bytes → build fails at SHA-512
  gate (fail-closed, intentional).
- The helper must run on unpatched Windows 10 21H2 LTSC (user requirement), so CETCompat is disabled (<CETCompat>false</CETCompat> in Directory.Build.props and Serpy.InstallerBootstrapper.csproj prevents fatal 0x80131506 CET mismatch on older shadow-stack-lacking kernels).
