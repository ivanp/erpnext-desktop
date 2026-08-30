# Planning Contract Recovery (splice into main plan)

## Planning Contract

### Key Technical Decisions

- KTD1. **Reframe by changing label maps in DashboardViewModel, not adding a mode.** Governs R10, R11.
- KTD2. **Setup screen replaces CredentialInputRequested wiring.** Governs R4, R6, R8.
- KTD3. **Load existing is a distinct dispatch path calling InspectArchiveAsync then AdoptAsync.** Governs R8.
- KTD4. **Routing and cheap archive-presence on ApplianceStatus; InspectArchiveAsync decrypts only small DPAPI metadata (never reads archive); AdoptAsync computes hash during streaming copy -- one total read.** Governs R2, R7, R13.
- KTD5. **Preserve-data build and adoption are two separate operations.** Governs R14.
- KTD6. **Three new IApplianceService methods in lockstep; none relaxes CanBuildFrom.** Governs R7, R8, R14.
- KTD7. **Unknown-provenance adoption is best-effort; only DPAPI CurrentUser provenance licenses real preflight.** Governs R7.
- KTD8. **Launch-on-finish via IShellDispatch2.ShellExecute or equivalent unelevated trampoline.** Governs R1, R16.
- KTD9. **RAW sparse-aware copy; hash during copy; journaled promotion; credential via File.Replace with backup.** Governs R7, R8.
