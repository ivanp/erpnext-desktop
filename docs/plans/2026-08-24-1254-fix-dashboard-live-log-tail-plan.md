---
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-plan-bootstrap
execution: code
---

# Dashboard Show-Details: Live Log Tail

## Goal Capsule

- **Objective:** The dashboard's "Show details" panel lets the user watch the appliance's real, verbose operation log update live while it grows, in a read-only, scrollable view that uses the window's available space — instead of the current panel, which shows only coarse stage messages and a literal log-file path, and appears empty most of the time.
- **Means:** Tail the per-operation log file — layering each operation's own narrative logging on top of `QemuProcess`'s existing stdout/stderr writes — by polling for new bytes, replacing the current progress-message accumulation into `DetailLog` (KTD1–KTD3).
- **Authority hierarchy:** This plan → `docs/plans/2026-08-22-0040-feat-qemu-erpnext-appliance-dotnet-avalonia-windows-host-plan.md` R17/AE8 (origin intent: technical logs behind an off-by-default Show-details toggle) → repo conventions (`AGENTS.md`).
- **Stop conditions:** Any implementation unit that would change QEMU's own log verbosity/output format, add log rotation/retention, or introduce a separate log-viewer window is out of scope — stop and flag rather than expanding into it.
- **Execution profile:** Standard. Four implementation units, single surface (`Serpy.Core` status contract + `Serpy.App` dashboard), no cross-cutting risk.
- **Tail ownership:** Implementer runs Verification Contract commands and confirms Definition of Done; no separate shipping tail beyond that.

## Product Contract

### Summary

Replace the dashboard's "Show details" disclosure — currently a dead-looking panel that shows only coarse per-stage progress text plus a bare log-file-path string — with a live, read-only tail of the real per-operation log file, filling the available window space with a working scrollbar.

### Problem Frame

R17/AE8 in the origin plan (`docs/plans/2026-08-22-0040-...windows-host-plan.md`) call for "technical logs/protocol detail" behind the Show-details toggle. The current implementation doesn't do that: `DashboardViewModel.DetailLog` only accumulates the `LogDetail` field of `OperationUpdate`, and every operation except `Build`'s single "provision" stage always reports `LogDetail: null` (`src/Serpy.Core/Operations/{Start,Stop,Initialize,Recover}Operation.cs`) — so for the four most common operations the panel never has real content while running. On completion, `Build`/`Recover` append `"\nLog: {path}"` — the literal file path, not its contents. `DetailLog` also resets to empty at the start of every dispatch, so an idle panel (no operation run yet, or reopened later) shows nothing. Separately, the panel is boxed into a fixed `MaxHeight="160"` `ScrollViewer` inside a 520×460 window regardless of how much room is available. Meanwhile every operation already writes its real stdout/stderr to a durable log file via `QemuProcess.Start(exe, args, logPath)` (`File.AppendAllText`, one open/append/close cycle per line) — that file is the "verbose log" the panel should be showing, **except `StopOperation`, which never spawns a `QemuProcess` at all and today never writes anything to its own `logPath` — the file is simply never created — and `BuildOperation`, which never persists `state.LogPath` to durable state outside its success-clearing path.**

### Requirements

- R1. The Show-details panel displays the real, verbose content of the current or most recently run operation's log file — never a bare file-path string, never only coarse stage messages.
- R2. While an operation is running, the panel behaves like `tail -f`: newly appended log lines appear without the user closing and reopening the panel.
- R3. The panel is read-only (view + select/copy text; no editing).
- R4. The panel fills the dashboard window's available vertical space rather than being capped at a small fixed height; the window remains user-resizable.
- R5. The panel has a visible, functional scrollbar for browsing log history.
- R6. When no operation has ever produced a log in the current install (fresh app, nothing run yet), the panel shows a clear placeholder — never an unexplained blank area indistinguishable from a broken control.
- R7. `Build`, `Stop`, `Recover`, and `Initialize` persist their own log path to durable state the same way `Start` already does, so R1's "most recently run operation's log" is accurate regardless of which operation ran last (today only `Start` does this; `Build` clears the path on success but never sets it; the others compute a local `logPath` but never persist it). `Restart` inherits this via its composed `Stop`+`Start` calls.
- R8. Every operation's log file has real, non-empty narrative content reflecting what that operation did — including `Stop`, which today spawns no subprocess and never writes to its log path at all.

### Success Criteria

Requirements ARE the success criteria here — each is independently verifiable (see Verification Contract).

### Scope Boundaries

Out of scope for this plan:
- Changing QEMU's own log verbosity or the format of its raw stdout/stderr lines (each operation's own narrative lines are layered alongside that raw output, not a replacement for it — see R8/KTD2).
- Log rotation, retention, or size limits.
- A standalone/detachable log-viewer window — the panel stays inside the dashboard window.
- Syntax highlighting, log-level coloring, or search/filter within the log text.
- Exporting or saving the log to another location (the file already exists on disk at a known path).
- Tail behavior in the tray `NativeMenu` — only the dashboard window's panel is affected.

## Planning Contract

### Key Technical Decisions

- **KTD1. Tail the real per-operation log file instead of the progress-message channel.** `ApplianceStatus` (the DTO `DashboardViewModel` polls every 3s via `App.axaml.cs`'s existing `DispatcherTimer`) gains a `string? LogPath` field, mapped from `ApplianceState.LogPath` in `StatusOperation.GetStatusAsync`. The panel derives its content by tailing `Status.LogPath`, not by accumulating `OperationUpdate.LogDetail`. *(Governs R1, R2, R6, R7.)*
- **KTD2. A single, per-path-synchronized `OperationLog` helper is the only writer to any operation's log file — including `QemuProcess`'s own stdout/stderr, not just each operation's narrative.** `StopOperation` never spawns a `QemuProcess`, so nothing writes to its `logPath` today. Worse, every operation's early validation guards (`Fail(...)` returns for a bad readiness state, a missing manifest, a rejected downgrade, etc.) return *before* the first `Report(...)` call, so today's `Report`-only instrumentation would still leave the most common failure paths — and Stop's and Start's no-op "nothing to do" success returns — with an empty or missing log file. Separately, `QemuProcess`'s `OutputDataReceived`/`ErrorDataReceived` handlers fire on async I/O completion threads while the operation's own `Report`/`Fail` calls run on the calling thread — genuinely concurrent writers to the same path that `LifecycleLock` (which only serializes *operations*, not intra-operation threads) does nothing to protect. Unsynchronized concurrent `File.AppendAllText` calls to one path risk interleaved lines or a transient sharing-violation `IOException`. The fix: `OperationLog.Append` takes an internal per-normalized-path lock around its open-write-close, and `QemuProcess`'s stdout/stderr handlers call it too (in place of their own direct `File.AppendAllText`), so every writer — QEMU output and every operation's own narrative — funnels through the one synchronized choke point. Unconditional coverage has three points: (a) log a `"[start] <Kind> started."` line immediately after computing `logPath`, before any guard; (b) each operation's existing local `Fail(id, msg, log)` helper also appends `msg`; (c) `Report(stage, msg, ...)` continues to append every intermediate progress line. Together these guarantee every terminal outcome, including the earliest guard rejection, leaves a real, human-readable, non-corrupted reason in the file. *(Governs R1, R8.)*
- **KTD3. Poll-based tail, not `FileSystemWatcher`.** All log writes now funnel through `OperationLog.Append` (KTD2) — one synchronized open/append/close per line, no persistent writer handle — which a watcher would need to correlate against unreliable rename/change events. Extend the app's existing polling architecture instead: read new bytes since a tracked byte offset on the same cadence already used for status refresh (3s). This is consistent with the one polling pattern the app already has, and `tail -f`'s user-facing requirement is "eventually catches up," not sub-second latency.
- **KTD4. Read with `FileShare.ReadWrite`, tolerate transient read failures.** Because a writer only briefly holds the file open per line, a concurrent tail read can occasionally race a write; on `IOException` the tail reader keeps its last-known offset and retries on the next poll tick rather than surfacing an error to the UI.
- **KTD5. Read-only `TextBox`, not `TextBlock` + `ScrollViewer`.** A read-only multi-line `TextBox` (`IsReadOnly="True"`, monospace font) is the standard Avalonia/desktop idiom for a log view: it gets a native scrollbar and text selection/copy for free, closing R3 and R5 without hand-rolled scroll plumbing.
- **KTD6. Panel fills available space; window grows to give it room by default.** Remove the fixed `MaxHeight="160"`; restructure the layout so the log panel occupies the remaining space in the (already-resizable) window when expanded, and increase the window's default height so the panel isn't cramped on first expand. *(Governs R4.)*
- **KTD7. "Stick to bottom" auto-scroll, with a small pixel tolerance.** New content auto-scrolls into view only while the user is already at, or within a small pixel tolerance of, the bottom of the panel (exact-equality bottom checks are fragile against sub-pixel/rounding drift); once they scroll up past that tolerance to read earlier output, new lines keep appending but the view does not jump back down until they scroll within tolerance of the bottom again. This is the standard `tail -f` / log-viewer convention and directly serves R2 without fighting a user who's mid-read. *(Governs R2.)*
- **KTD8. Explicit placeholder text for the no-log-yet state**, rather than an empty string, so the panel is legibly "nothing here yet" instead of looking broken. *(Governs R6.)*

### High-Level Technical Design

Data flow from the real log file on disk to the live panel, and the two states the panel can be in:

```
Operation's own Report/Fail() and QemuProcess's stdout/stderr handler (any thread)
  └─ OperationLog.Append(logPath, stage, msg)  [KTD2 — single synchronized writer; covers ops with no QemuProcess, e.g. Stop, and races between QEMU's async callbacks and the operation's own thread]
                                                          │
        ┌─────────────────────────────────────────────────┘
        ▼
ApplianceState.LogPath (persisted)              [KTD1 — now written by every op, not just Start]
        │  (StateStore)
        ▼
StatusOperation.GetStatusAsync()
        │  maps state.LogPath → ApplianceStatus.LogPath   [KTD1]
        ▼
App.axaml.cs DispatcherTimer (existing 3s tick)
        │  RefreshStatusAsync() → DashboardViewModel.Status
        ▼
DashboardViewModel (per tick)
        │  if Status.LogPath changed → reset tracked offset, clear panel text   [KTD3]
        │  LogTailReader.ReadNewContent(path, offset) → (newText, nextOffset)   [KTD3, KTD4]
        │  append newText to panel-bound text
        ▼
DashboardWindow.axaml — read-only TextBox, fills available space   [KTD5, KTD6]
        │  ScrollChanged: track "at bottom?"; TextChanged: scroll to end only if at bottom   [KTD7]
        ▼
User sees live-updating, scrollable, read-only log (or placeholder text when Status.LogPath is null)  [KTD8]
```

Panel state machine (two states, driven entirely by `Status.LogPath`):

```
                Status.LogPath == null
      ┌───────────────────────────────────┐
      │            Empty/placeholder        │
      └───────────────────────────────────┘
                        │ Status.LogPath becomes non-null
                        ▼
      ┌───────────────────────────────────┐
      │   Tailing <LogPath>                 │──┐ same LogPath next tick → append new bytes
      │   (live while operation runs;       │◄─┘
      │    static once file stops growing)  │
      └───────────────────────────────────┘
                        │ Status.LogPath changes to a *different* path
                        ▼
                 reset offset/text, tail the new path
```

### Assumptions

- The 3-second poll cadence already used for status refresh is an acceptable tail latency; no dedicated faster timer is introduced (KTD3).
- `ApplianceState.LogPath` values are always local filesystem paths owned by this app (never remote/network paths), so plain `FileStream` reads are sufficient — no special handling for slow/networked storage.
- Log files are text (UTF-8, as written by `File.AppendAllText`'s default encoding); no binary content to guard against.

## Implementation Units

### U1. Give every operation a real, persisted log path with real log content

**Goal:** Every operation (not only `Start`) persists its own log path to durable state as the very first thing it does, and every operation's log file has real, non-empty narrative content for every terminal outcome — including an early guard rejection, a no-op success, or `Stop`, which spawns no `QemuProcess` at all (R1, R7, R8; KTD1, KTD2).

**Requirements:** R1, R7, R8. Instantiates KTD1, KTD2.

**Files:**
- `src/Serpy.Core/Operations/OperationLog.cs` (new)
- `src/Serpy.Core/Qemu/QemuProcess.cs`
- `src/Serpy.Core/Contracts/ApplianceStatus.cs`
- `src/Serpy.Core/Operations/StatusOperation.cs`
- `src/Serpy.Core/Operations/BuildOperation.cs`
- `src/Serpy.Core/Operations/StopOperation.cs`
- `src/Serpy.Core/Operations/RecoverOperation.cs`
- `src/Serpy.Core/Operations/InitializeOperation.cs`
- `src/Serpy.Core/Operations/StartOperation.cs`
- `tests/Serpy.Core.Tests/Operations/OperationLogTests.cs` (new)
- `tests/Serpy.Core.Tests/Operations/StatusOperationTests.cs` (new)
- `tests/Serpy.Core.Tests/Operations/StartOperationTests.cs`, `RecoverOperationTests.cs`, `InitializeOperationTests.cs` (extend)

**Approach:**
1. Add a static `OperationLog.Append(string logPath, string stage, string msg)` helper that appends `"[{stage}] {msg}\n"` to `logPath` (creating the file/directory if needed), synchronized per normalized path (e.g. a lock object keyed by `Path.GetFullPath(logPath)` in a small internal registry) so concurrent callers on different threads never interleave or race a write to the same file.
2. In `QemuProcess.Start`, change the `OutputDataReceived`/`ErrorDataReceived` handlers to call `OperationLog.Append(logFile, "stdout"/"stderr", e.Data)` instead of their own direct `File.AppendAllText` — this is what makes QEMU's async-callback-thread writes and an operation's own same-thread `Report`/`Fail` writes to the same file safe against each other.
3. In every operation (`Build`, `Initialize`, `Start`, `Stop`, `Recover`), as the very first statements after `logPath` is computed — before any guard/validation check — call `OperationLog.Append(logPath, "start", "<Kind> started.")` and persist `state.LogPath = logPath` via `stateStore.Mutate`. This is a move, not just an addition, for `StartOperation`: today it sets `state.LogPath` only after its readiness/recovery-journal/manifest guards already passed, so an early Start failure never even points at its own log file.
4. Call `OperationLog.Append` from each operation's existing `Report(stage, msg, ...)` local function, alongside the existing `progress.Report(...)` call — covers every intermediate progress line.
5. Modify each operation's existing local `Fail(Guid id, string msg, string log)` static helper to also call `OperationLog.Append(log, "failed", msg)` before constructing the `OperationResult` — covers every guarded and late failure return uniformly, without editing each individual `return Fail(...)` call site.
6. `StopOperation`'s "no running QEMU process recorded" early return and `StartOperation`'s "already running" early return don't go through `Fail` (they're no-op successes) — add one `OperationLog.Append(logPath, "info", msg)` call at each of those two specific return sites so a no-op run still explains itself in the log.
7. Add `string? LogPath` to the `ApplianceStatus` record; map `state.LogPath` straight through in `StatusOperation.GetStatusAsync`.
8. Do not add any new clearing logic beyond what `BuildOperation.RecordAcceptedBuild` already has — `LogPath` persists across idle time by design (R1), only changing when a new operation starts.

**Test Scenarios:**
- `OperationLog.Append` creates the log file (and its directory) if it doesn't exist yet, and appends `"[stage] msg\n"` for each call.
- `OperationLog.Append` called twice appends both lines in order (second call doesn't overwrite the first).
- Many concurrent threads calling `OperationLog.Append` on the same path in parallel (e.g. 20 threads, a few lines each) produce a file whose total line count equals the total number of calls, with no interleaved/corrupted lines and no exception from any caller — the concurrency case this plan exists to close.
- `GetStatusAsync` returns `LogPath` equal to `state.LogPath` when state has one set.
- `GetStatusAsync` returns `LogPath: null` when state has never had one set (fresh install).
- `StopOperation` run with no QEMU process recorded (the no-op path) still produces a non-empty log file and persists `state.LogPath` — the exact case this plan exists to fix.
- `StartOperation` rejected by an early guard (e.g. wrong readiness state) still produces a non-empty log file explaining the rejection, and persists `state.LogPath` to that attempt's own log — not left unset.
- After `BuildOperation`/`RecoverOperation`/`InitializeOperation` runs (success or failure), `state.LogPath` equals that operation's own log file path (not left over from a prior `Start`), and the file at that path is non-empty.
- After a successful `Start` followed by an idle period (no further operation), `state.LogPath` is unchanged and still resolves via `GetStatusAsync`.

### U2. `LogTailReader`: incremental, share-tolerant file tail primitive

**Goal:** A small, independently testable component that reads only the new bytes appended to a file since a given offset, without holding a persistent lock, and tolerates the writer's file having briefly been touched (KTD3, KTD4).

**Requirements:** R1, R2. Instantiates KTD3, KTD4.

**Files:**
- `src/Serpy.App/Services/LogTailReader.cs` (new)
- `tests/Serpy.App.Tests/Services/LogTailReaderTests.cs` (new)

**Approach:** A method taking a file path and a starting byte offset, opening the file with `FileAccess.Read, FileShare.ReadWrite`, seeking to the offset, reading to end-of-file as text, and returning the new text plus the resulting offset. Missing file (not yet created) and `IOException` both return "no new content, same offset" rather than throwing, so callers can poll unconditionally without their own try/catch.

**Test Scenarios:**
- Reading from offset 0 on a file with existing content returns that full content and an offset equal to the file's length.
- Appending more text to the file and reading again from the previous offset returns only the newly appended text.
- Reading a path that does not exist returns empty content and the offset unchanged (no exception).
- Reading with an offset already at end-of-file returns empty content and the same offset (no new bytes).
- A file opened for writing by another handle at read time (simulated via a held write lock) does not throw out of the reader — it returns unchanged offset/empty content for that call.

### U3. Wire live tail into `DashboardViewModel`

**Goal:** On each status poll tick, keep the panel's text in sync with the current `Status.LogPath` using `LogTailReader`, replacing the old progress-message accumulation (KTD1, KTD3, KTD8).

**Requirements:** R1, R2, R6. Instantiates KTD1, KTD8.

**Files:**
- `src/Serpy.App/ViewModels/DashboardViewModel.cs`
- `tests/Serpy.App.Tests/ViewModels/DashboardViewModelTests.cs`

**Approach:**
1. Remove the `DetailLog +=` lines inside `DispatchAsync`, `DispatchInitializeAsync`, and `DispatchRecoverAsync` (the `\nLog: {lp}` path-echo and the `ApplyUpdate`-driven `LogDetail` accumulation) — the panel no longer derives content from the progress channel.
2. In `RefreshStatusAsync` (already called every poll tick), when `Status.LogPath` differs from the previously observed path, reset the tracked read offset to 0 and clear the panel text; otherwise call the injected tail reader with the tracked offset and append any returned text.
3. Inject the tail-reading function (defaulting to `LogTailReader`) the same way `IApplianceService` is injected (`SetService`), so tests can substitute a fake reader without real file timing.
4. Expose the placeholder text (KTD8) as a computed property when `Status.LogPath` is null and the tail buffer is still empty.

**Test Scenarios:**
- First poll tick with a non-null `Status.LogPath` and a fake reader returning content appends that content to the panel text.
- A later tick with the same `Status.LogPath` and new fake-reader content appends only the new content (offset carried forward, not reset).
- A tick where `Status.LogPath` changes to a different path resets the tracked offset and panel text before tailing the new path.
- `Status.LogPath == null` and no tail content yet yields the placeholder text, not an empty string.
- Dispatching an operation no longer mutates the panel text directly (removed `DetailLog +=` call sites) — the panel only changes via the polled tail.

### U4. Rework the Show-details panel layout and scroll behavior

**Goal:** Read-only, full-space, scrollable panel with stick-to-bottom auto-scroll (R3–R6, KTD5–KTD7).

**Requirements:** R3, R4, R5, R6. Instantiates KTD5, KTD6, KTD7.

**Files:**
- `src/Serpy.App/Views/DashboardWindow.axaml`
- `src/Serpy.App/Views/DashboardWindow.axaml.cs`

**Approach:**
1. Replace the `TextBlock` inside a `MaxHeight="160"` `ScrollViewer` with a read-only, monospace, multi-line `TextBox` bound to the panel text, laid out to fill the remaining vertical space when the `Expander` is open (removing the fixed `MaxHeight`).
2. Increase the window's default `Height` (and, if needed, `MinHeight`) so the panel has visible room by default; the window stays user-resizable (no `CanResize="False"`).
3. In code-behind, track whether the view is scrolled to (or near) the bottom before each text update, and only force-scroll to the end when it was — implementing KTD7's stick-to-bottom behavior. Avalonia's `TextBox` doesn't expose this via binding alone, so this is code-behind logic, not XAML.

**Test Scenarios:** This is Avalonia XAML layout plus a small amount of view code-behind scroll-position logic with no independent business rule to unit-test in isolation from the framework; the behavior is verified via the manual/smoke check in the Verification Contract rather than a headless unit test. *(Test expectation: none — layout/view-behavior unit, verified by smoke check below.)*

## Verification Contract

- `dotnet build Serpy.slnx -c Release` — must succeed with 0 errors before running tests.
- `dotnet test Serpy.slnx -c Release --no-build --filter "FullyQualifiedName!~IntegrationTests"` — covers U1 (`Serpy.Core.Tests`) and U2/U3 (`Serpy.App.Tests`); all new and existing tests pass.
- Manual/smoke check for U4 (no headless Avalonia rendering harness is in place for this view): launch `Serpy.App`, run any operation (e.g., `Start`), expand "Show details", and confirm — content is the real log file's text (not a path string), it grows live while the operation runs, the window/panel fills available space and remains resizable, a scrollbar appears once content exceeds the visible area, scrolling up stops auto-scroll and scrolling back to the bottom resumes it, and reopening the panel after an operation completes (or on a fresh idle app) shows the last log or the placeholder text rather than a blank area.

## Definition of Done

- All four units implemented; Verification Contract commands pass.
- No implementation unit changed QEMU's own log verbosity or the format of its raw stdout/stderr lines, or added rotation/retention/log-viewer-window/export scope (Scope Boundaries honored). Each operation's own narrative logging (KTD2) and the tail/UI rework are the intended, in-scope changes to log plumbing.
- Dead code from the old progress-message-accumulation approach (removed `DetailLog +=` call sites, any now-unused `LogDetail` plumbing that has no other caller) is cleaned up, not left alongside the new tail-based path.
- `docs/results.md` updated with a short verification entry for this change (manual smoke-check outcome), per repo convention for tracking verification outcomes.
