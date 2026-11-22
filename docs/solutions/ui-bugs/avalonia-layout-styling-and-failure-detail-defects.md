---
title: Avalonia 11 dashboard layout, FluentTheme styling, and failure-detail isolation defects in Serpy.App
date: 2026-11-22
category: ui-bugs
module: Serpy.App
problem_type: ui_bug
component: frontend
severity: high
symptoms:
  - "Everyday dashboard header, status badge, progress bar, action buttons, autostart checkbox and expander all rendered stacked on top of each other at the window origin (0,0)"
  - "Style classes used by the views (h1, sub, muted, fact, primary, accent, sm, big) had no effect because App.axaml only loaded FluentTheme"
  - "Healthy status badge stayed gray (background, text and LED dot) even though the Border carried the ok class"
  - "Expander content shrink-wrapped to ~350px clipping the details card, and the '▾ Hide details' header was painted over a solid blue rectangle"
  - "CannotRun/Problem screens showed raw Core messages ('Port 18080...', 'Insufficient free disk space...') and a duplicated 'Now: Now: ...' prefix on the activity line"
root_cause: wrong_api
resolution_type: code_fix
related_components:
  - SetupViewModel
  - DashboardViewModel
  - UiText
  - LayoutInvariantTests
tags:
  - avalonia
  - dockpanel
  - styles
  - controltheme
  - fluenttheme
  - value-precedence
  - xaml
  - layout-invariants
framework_version: "dotnet 10.0 / avalonia 11"
---

# Avalonia 11 dashboard layout, FluentTheme styling, and failure-detail isolation defects in Serpy.App

## Problem

Five related defects in the `Serpy.App` Avalonia 11 UI regressed or surfaced during the U9/U10 guided-setup work and were fixed on the `development` branch (pushed to `origin/development` at `81e0db1`; fix chain `a3e4dfc` → `921ffb8` → `d3a2f5d` → `a19f876` → `6690609` → `ea0c895` → `81e0db1`):

1. **`DockPanel` children that were not its children** (`src/Serpy.App/Views/EverydayView.axaml`, pre-fix state at `a3e4dfc~1`). The whole dashboard was wrapped in `<Panel Name="DashboardPanel">` inside a `<DockPanel>`, and every section carried `DockPanel.Dock="Top"` / `"Bottom"`. `DockPanel` only reads the `Dock` property of its **direct** children — it saw exactly one child (the `Panel`), so all five docked sections were ignored and stacked on top of each other at `(0,0)`.
2. **`App.axaml` loaded only `<FluentTheme />`** (`src/Serpy.App/App.axaml`, pre-fix lines 43–45 at `a3e4dfc~1`). Every style class the views reference — `h1`, `sub`, `muted`, `hint`, `fact`, `primary`, `accent`, `big`, `sm` — had no definition, so views designed against `docs/plans/mockups/2026-11-20-easy-install/easy-install-mockups.html` rendered with unstyled FluentTheme defaults.
3. **Local attribute values beat style setters.** The status-badge `Border` carried a local `Background="#EEF1F4"`, `TextBlock#StatusBadgeText` a local `Foreground="#6B7280"`, and `Border#LedDot` a local `Background="#9AA3AD"` (post-`a3e4dfc` state, removed in `921ffb8`). In Avalonia's value precedence, a local value always wins over a style setter, so `<Style Selector="Border.ok">` setters never applied and the healthy badge stayed gray.
4. **FluentTheme `Expander`/`ToggleButton` template collisions.** The default `Expander` `ControlTheme` shrink-wrapped content (details clipped to ~350px in a 560px window) with the chevron floated far right, and — after a custom `Expander` `ControlTheme` was introduced — FluentTheme's global `ToggleButton:checked /template/ ContentPresenter#PART_ContentPresenter` selector painted a solid blue rectangle behind `▾ Hide details`, because the custom `ToggleButton` template reused the reserved part name `PART_ContentPresenter`.
5. **Raw failure messages leaked onto the default screens** (`SetupViewModel.ApplyFailureText` was `FailureDetail = failureMessage ?? f.Detail`, changed in `ea0c895`). Core-layer messages such as `Port 18080 is already used...` or `Insufficient free disk space...` appeared on the plain-word Problem/CannotRun screens, violating AE23/R39 (technical terms live only under Show details / the Operation Log). `SetupView.axaml` at `a3e4dfc` also duplicated a literal `"Now: "` `TextBlock` before `ActivityText`, which the view model already formats through `UiText.NowLine` (`"Now: {0}"`), producing `"Now: Now: ..."`.

All five are fixed in the current tree, verified by `dotnet build Serpy.slnx -c Release` (0 warnings) and 694/694 deterministic unit tests (`Serpy.Core.Tests` 509, `Serpy.App.Tests` 165, `Serpy.InstallerBootstrapper.Tests` 20), including `LayoutInvariantTests`, `UiTextTests`, and `SetupViewModelTests`, plus headless visual screenshot inspection.

## Symptoms

- Dashboard header (brand + badge + loopback URL), stage text/progress bar, action buttons, autostart checkbox, and the expander were all painted overlapping at the window's top-left corner.
- Typography/spacing/color classes (`h1`, `muted`, `primary`, `fact`, `accent`) had no visible effect anywhere in the app.
- With ERPNext healthy, the badge pill remained gray (`#EEF1F4` background, gray text/dot) instead of green (`#E7F6EC` / `#1C6B3A` / `#2E9E5B`).
- The "Show details" expander content was clipped to roughly 350px wide, and its expanded header showed the label on a solid blue box.
- Setup failure screens displayed engineer-facing strings (`"Port 18080..."`, `"Insufficient free disk space..."`, `"Wrong size..."`) above a lowercase what-to-do fragment with no bold label, and the Installing screen's activity line read `"Now: Now: adding ERPNext parts, 214 of 489"`.

## What Didn't Work

1. **Keeping local color attributes next to the class styles.** After `a3e4dfc` the badge read:
   ```xml
   <Border Classes="badge" Classes.ok="{Binding IsHealthy}" Classes.stopped="{Binding !IsHealthy}"
           Background="#EEF1F4">                        <!-- local value, always wins -->
     <Border.Styles>
       <Style Selector="Border.ok"><Setter Property="Background" Value="#E7F6EC"/></Style>
   ```
   Adding *more* `Border.ok` styles could never fix this: a local value has higher precedence than any style setter in Avalonia. The fix was to delete the local attributes (`Background`, `Foreground`, LED dot `Background`) entirely and express every state color as styles scoped under the `badge` class (`921ffb8`, now `src/Serpy.App/Views/EverydayView.axaml:29-66`).

2. **Reusing `PART_ContentPresenter` inside the custom `ToggleButton` template.** The first cut of the custom `Expander` `ControlTheme` (`a3e4dfc`–`921ffb8`) named the header `ToggleButton`'s content presenter `PART_ContentPresenter`. FluentTheme ships a global style `ToggleButton:checked /template/ ContentPresenter#PART_ContentPresenter` that fills that presenter blue when checked — so every expanded "Hide details" header got a solid blue rectangle behind the text. Renaming the presenter to `HeaderToggleContent` with an explicit transparent background in `d3a2f5d` (`src/Serpy.App/App.axaml:85-88`) removed the collision; `PART_`-prefixed names are part-name contracts, not free-form names.

3. **Asserting `IsVisible` for screen roots in headless layout tests.** The first `LayoutInvariantTests` (`a3e4dfc`) asserted `welcome.IsVisible`, `checking.IsVisible`, etc. Those `StackPanel` screen roots sit inside `ScrollViewer`s whose `IsVisible` carries the binding; a child's own `IsVisible` stays `true` when only an ancestor is collapsed, so the assertions were vacuously green. `6690609` added `IsVisible="{Binding ShowX}"` on each screen root (e.g. `src/Serpy.App/Views/SetupView.axaml:20`, `:75`, `:97`, `:145`, `:178`, `:221`, `:262`, `:345`, `:398`) and switched the checks to `IsEffectivelyVisible`, which walks the ancestors (`tests/Serpy.App.Tests/LayoutInvariantTests.cs:66-82`).

4. **Switching the test host to real rendering to "see" layout.** `a3e4dfc` temporarily changed `tests/Serpy.App.Tests/AppTestHost.cs` to `.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })`. That combination is only useful for capturing actual screenshots during visual inspection; it is not needed for the geometry invariants (bounds, intersection, effective visibility all work under plain headless layout) and was reverted in `a19f876` (`tests/Serpy.App.Tests/AppTestHost.cs:10-13` now uses plain `.UseHeadless(new AvaloniaHeadlessPlatformOptions())`).

5. **Surfacing the Core failure message directly on the default screen.** `FailureDetail = failureMessage ?? f.Detail` seemed user-friendly ("show what actually happened") but put banned technical vocabulary on plain-word screens. The correct split is: plain headline/detail/what-to-do from the `UiText.Failure` table on the screen, raw message appended into the Operation Log tail (`DetailLogText`) reachable under Show details (`ea0c895`).

## Solution

### 1. Dashboard layout: `Panel`-in-`DockPanel` → flat `StackPanel`

Before (`git show a3e4dfc~1:src/Serpy.App/Views/EverydayView.axaml`, lines 6–10):

```xml
<DockPanel Margin="20">
  <Panel Name="DashboardPanel">
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Spacing="12" Margin="0,0,0,14">
      <!-- header ... -->
```

`DockPanel.Dock` was also set on the progress row (line 24), button row (line 30), checkbox (line 51) and expander (line 60, `Dock="Bottom"`) — all inside the `Panel`, plus a trailing empty `<Grid/>` fill child. None of the dock hints had any effect.

After (`src/Serpy.App/Views/EverydayView.axaml:7-8`): the `DockPanel` is gone; the layout is a `ScrollViewer` → vertical `StackPanel` with explicit `Spacing`, keeping the `DashboardPanel` name for the automation tree:

```xml
<ScrollViewer>
  <StackPanel Name="DashboardPanel" Margin="22,18,22,20" Spacing="14">
```

Every section is now a direct child in document order, so the header (`:11-86`), subtitle (`:89`), stage/progress (`:92-102`), action row (`:105-131`), checkbox (`:134-139`) and the top-bordered expander block (`:142-199`) stack vertically and scroll.

### 2. `App.axaml`: palette + style classes + custom `Expander` `ControlTheme`

Before, `Application.Styles` contained only `<FluentTheme />` (`src/Serpy.App/App.axaml`, pre-fix lines 43–45 at `a3e4dfc~1`).

After (`src/Serpy.App/App.axaml`):

- **Palette** as `Color`/`SolidColorBrush` resources (`:7-61`): brand green `#2F6F4F`, ink `#1A1D21`, muted `#6B7280`, line `#E3E6EA`, ok/warn/bad sets, log-panel `#10151B`/`#CBD5E1`.
- **Typography styles** (`:174-199`): `TextBlock.h1`, `.sub`, `.muted`, `.hint`, `.fact`.
- **Button styles** (`:202-254`): base white/rounded style, `:pointerover` template overrides (`:215`), `.primary` and `.accent` brand fills with hover states (`:221-243`), `.big` and `.sm` sizes.
- **ProgressBar / CheckBox** (`:257-278`), including branded `CheckBox:checked /template/ Border#NormalRectangle`.
- **A full custom `Expander` `ControlTheme`** (`:64-131`) replacing the FluentTheme one (see §4).
- `<FluentTheme />` stays as the base (`:171`), so the overrides layer on top of it.

### 3. Status badge: no local values, state styles scoped by `badge` class

Before (`921ffb8` diff, pre-state): local `Background="#EEF1F4"` on the `Border`, local `Foreground="#6B7280"` on `TextBlock#StatusBadgeText`, local `Background="#9AA3AD"` on `Border#LedDot`, with sibling `Border.ok` styles that could never win.

After (`src/Serpy.App/Views/EverydayView.axaml:22-76`):

```xml
<Border Grid.Column="1" Margin="14,0,0,0" CornerRadius="999" Padding="12,5"
        VerticalAlignment="Center"
        Classes="badge"
        Classes.ok="{Binding IsHealthy}"
        Classes.warn="{Binding IsStartingOrUnhealthy}"
        Classes.bad="{Binding IsCrashed}"
        Classes.stopped="{Binding !IsHealthy}">
  <Border.Styles>
    <Style Selector="Border.badge"><Setter Property="Background" Value="#EEF1F4" /></Style>
    <Style Selector="Border.badge TextBlock#StatusBadgeText"><Setter Property="Foreground" Value="#6B7280" /></Style>
    <Style Selector="Border.badge Border#LedDot"><Setter Property="Background" Value="#9AA3AD" /></Style>
    <Style Selector="Border.badge.ok"><Setter Property="Background" Value="#E7F6EC" /></Style>
    <Style Selector="Border.badge.ok TextBlock#StatusBadgeText"><Setter Property="Foreground" Value="#1C6B3A" /></Style>
    <Style Selector="Border.badge.ok Border#LedDot"><Setter Property="Background" Value="#2E9E5B" /></Style>
    <!-- ... warn / bad states likewise ... -->
  </Border.Styles>
  <StackPanel Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
    <Border Name="LedDot" Width="10" Height="10" CornerRadius="5" VerticalAlignment="Center" />
    <TextBlock Name="StatusBadgeText" AutomationProperties.AutomationId="StatusBadge"
               Text="{Binding StatusBadgeText}" FontSize="13" FontWeight="SemiBold" VerticalAlignment="Center" />
  </StackPanel>
</Border>
```

The class-feeding properties live on `DashboardViewModel` (`src/Serpy.App/ViewModels/DashboardViewModel.cs:85-87`: `IsHealthy`, `IsStartingOrUnhealthy`, `IsCrashed`), and the badge label itself is `StatusBadgeText` (`:123-151`), which switches between plain sentences (`UiText.ErpNextIsReady`, …) and technical detail when `ShowDetails` is on. `IsEffectivelyVisible`-based assertions plus class checks guard this in `tests/Serpy.App.Tests/LayoutInvariantTests.cs:307-314` (badge is `Running`, its border `Contains("ok")`, address line `"{loopback} · works only on this PC"` from `AddressDisplayText`, `DashboardViewModel.cs:105-109`; `DataFolderText` at `:111`).

### 4. Expander: custom `ControlTheme` with renamed header presenter

The custom theme (`src/Serpy.App/App.axaml:64-131`):

- Stretch setters on the Expander (`:66-67`), transparent/zeroed chrome (`:68-70`).
- The header `ToggleButton` template's presenter is named `HeaderToggleContent` with `Background="Transparent"` (`:85-88`) — **not** `PART_ContentPresenter`, which was the blue-box bug. Renamed in `d3a2f5d`, together with an explicit `^ /template/ ToggleButton#ExpanderHeader { Background: Transparent; BorderThickness: 0 }` style (`:124-127`).

```xml
<!-- Before: FluentTheme's ToggleButton:checked selector paints PART_ContentPresenter blue -->
<ContentPresenter Name="PART_ContentPresenter" ... />

<!-- After: non-contract name isolates the header from FluentTheme's checked fill -->
<ContentPresenter Name="HeaderToggleContent" Background="Transparent" ... />
```

- Chevron `▸`/`▾` swapped by `^:expanded` (`:93-98`, `:128-130`), accent-colored header via `PART_HeaderPresenter` bound to `TemplateBinding Header` (`:99-105`).
- Content host `Border` with `IsVisible="{TemplateBinding IsExpanded}"` (`:109-114`) wrapping the real `PART_ContentPresenter` (`:115-119`) — this is the only `PART_` name, it lives in the Expander (not `ToggleButton`) template, so FluentTheme's `ToggleButton:checked` selector cannot reach it.
- Global `Expander` stretch overrides (`:281-284`); both usages set `HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch"` (`src/Serpy.App/Views/EverydayView.axaml:146-147`, `src/Serpy.App/Views/DetailsPanel.axaml:17-18`).

Regression guard: `tests/Serpy.App.Tests/LayoutInvariantTests.cs:339-349` asserts the expanded expander is wider than 450px in a 560px window (not shrink-wrapped to ~350px) and that the `HeaderToggleContent` presenter's background is null/transparent (no blue box).

### 5. Failure text: plain words on screen, raw message in the Operation Log tail

`src/Serpy.App/Text/UiText.cs` is the single wording table: `Plain(...)` records every screen string in a registry (`:25-29`) that `UiTextTests` scans against AE23 banned terms (`tests/Serpy.App.Tests/UiTextTests.cs:11-15` and `:27-35`), `NowLine` formats `"Now: {0}"` (`:175-179`), `WhatToDoLabel` is `"What to do:"` (`:219`), and `Failures` maps every `FailureCode` to a plain `Headline/Detail/WhatToDo` record (`:238-274`).

Before (`ea0c895` diff): `FailureDetail = failureMessage ?? f.Detail;` — raw Core message wins.

After (`src/Serpy.App/ViewModels/SetupViewModel.cs:729-747`):

```csharp
private void ApplyFailureText(FailureCode failure, string? failureMessage)
{
    var f = UiText.Failure(failure == FailureCode.None ? FailureCode.Unknown : failure);
    FailureHeadline = f.Headline;
    FailureDetail = f.Detail;          // always the plain sentence
    FailureWhatToDo = f.WhatToDo;

    if (!string.IsNullOrWhiteSpace(failureMessage) &&
        !_detailsBuffer.ToString().Contains(failureMessage, StringComparison.Ordinal))
    {
        if (_detailsBuffer.Length > 0) _detailsBuffer.AppendLine();
        _detailsBuffer.Append(failureMessage);
        DetailLogText = _detailsBuffer.ToString();   // raw message lives under Show details
    }
}
```

Callers keep cancellation silent (`ApplySetupResult` passes `result.Outcome == OperationOutcome.Cancelled ? null : result.Message`, `:724`; the cancelled-task path passes `null`, `:705`). `ApplySetupUpdate` initializes the tail **before** applying run state (`:313-315`: `StartDetailsTail(update.RunId)` now precedes `ApplyRunState(update)`), so a failure append cannot be wiped by the fresh-run buffer reset (`StartDetailsTail` clears `_detailsBuffer` and `DetailLogText`, `:749-761`).

Screen markup: the CannotRun banner and Problem banner render the bold `What to do:` run before the lowercase continuation (`src/Serpy.App/Views/SetupView.axaml:118-122`, `:420-424`), as do the RestartNeeded (`:154-158`) and quiet-step banner (`:295-299`) screens; the Installing screen's now-line box binds a single `ActivityText` to the VM value with no literal `"Now: "` prefix (`:317-328`; the duplicate `TextBlock Text="Now: "` that existed at `a3e4dfc`, lines 301–304, was removed in `921ffb8`).

`tests/Serpy.App.Tests/ViewModels/SetupViewModelTests.cs:258-273` (`FailureMessage_KeepsRawMessageInDetailsLogAndNotDefaultScreen`) pins the contract with a real-world message: `FailureDetail` equals `UiText.Failure(PortInUse).Detail`, contains neither `"Port"` nor `"18080"`, and `DetailLogText` contains `"Port 18080"`. `EveryFailureCode_ShowsHeadlineDetailWhatToDo` (`:242-256`) covers all codes; `tests/Serpy.App.Tests/LayoutInvariantTests.cs:281-284` asserts the Problem screen's what-to-do text has a bold inline.

### Fix commit map (all on `development`, pushed to `origin/development` at `81e0db1`)

| Commit | What it fixed |
|---|---|
| `a3e4dfc` | Mockup rewrite of Setup/Everyday views, palette + styles + first Expander `ControlTheme`, `LayoutInvariantTests` (with the temporary Skia test-host override) |
| `921ffb8` | Expander stretch, badge state styles without local attributes, `SubtitleText`/`DetailsHeaderText` bindings, `"Now: "` duplicate removal, bold `WhatToDoLabel` runs |
| `d3a2f5d` | `HeaderToggleContent` rename (blue-box fix) + transparent `ExpanderHeader` style + restart-advice prefix |
| `a19f876` | Reverted `AppTestHost` to plain headless |
| `6690609` | Screen roots own `IsVisible` bindings; `IsEffectivelyVisible` assertions; everyday wording/badge-class cleanup |
| `ea0c895` | `ApplyFailureText` isolation + tail ordering + regression test |
| `81e0db1` | Docs: deterministic test counts and UiTests status |

## Why This Works

- **`DockPanel` is a layout panel for its direct children.** It measures its `Children` collection and reads the `DockPanel.Dock` attached property on each of *those* elements only; a `Dock` value on a grandchild is inert. Wrapping everything in one `Panel` therefore collapsed the whole dock contract. A vertical `StackPanel` matches the mockup's top-to-bottom flow and needs no dock hints.
- **Avalonia's property-value precedence puts local values above style setters.** `Background="#EEF1F4"` as an attribute is a local value; no `Style` can override it. Moving every state color into named styles under the `badge` class makes the state selectors (`Border.badge.ok`, `.warn`, `.bad`, `.stopped`) authoritative, and the longer selectors (`Border.badge.ok TextBlock#StatusBadgeText`) are more specific than the base ones, so the cascade resolves correctly.
- **Template part names are a contract, not a namespace you own.** FluentTheme ships pseudo-class styles like `ToggleButton:checked /template/ ContentPresenter#PART_ContentPresenter`; any `ToggleButton` template that names a presenter `PART_ContentPresenter` inherits that paint. Naming the custom presenter `HeaderToggleContent` (and pinning it transparent) takes it out of FluentTheme's selectors, while keeping the genuine `PART_ContentPresenter` on the `Expander` content host preserves the pattern where the framework expects it.
- **`IsEffectivelyVisible` vs `IsVisible`.** `IsVisible` reports only the control's own flag; a control inside a collapsed `ScrollViewer` still reports `IsVisible == true`. `IsEffectivelyVisible` walks the visual ancestors, which is the assertion that matches what a user can see — and it caught the second-order bug that the screen `StackPanel`s themselves needed their own `IsVisible` bindings.
- **AE23/R39 word isolation works because it is enforced structurally.** `UiText.Plain` funnels every default-screen string into one registry that a unit test scans for banned technical terms (`QEMU`, `port`, `qcow2`, `.img`, …), and the view models may only *append* raw diagnostics into the Operation Log tail (`_detailsBuffer`/`DetailLogText`) that lives under Show details — the same log the Save details button exports. Ordering `StartDetailsTail` before `ApplyRunState` keeps the append from landing in a just-cleared buffer.

## Prevention

1. **Attached layout properties (`DockPanel.Dock`, `Grid.Row`/`Grid.Column`) are read only by the element's immediate parent panel.** Wrapping docked elements in an intermediate container (`Panel`, `Border`) silently turns those attached properties into no-ops; prefer a vertical `StackPanel` for top-to-bottom flow, and rely on `LayoutInvariantTests` bounds/overlap checks to catch stacking regressions.
2. **Never set a local attribute and a class style for the same property.** If a control has `Classes.ok`/`Classes.warn`-style state, every variant value must live in the styles, or the state styling silently dies. Layout tests should assert classes (`Assert.Contains("ok", border.Classes)`), not just bindings.
3. **Treat `PART_`-prefixed template names as reserved.** Inside custom `ControlTheme`s/`ControlTemplate`s, use your own presenter names unless you are deliberately implementing that control's documented parts.
4. **Assert layout with `IsEffectivelyVisible` plus geometry bounds**, and give each screen root its own `IsVisible` binding instead of relying on an ancestor's. `LayoutInvariantTests` (`tests/Serpy.App.Tests/LayoutInvariantTests.cs`) now encodes: only the active screen effectively visible (`:66-82`), no fact-item intersection (`:106-118`), buttons within window client size (`:120-124`), expander width and transparent header presenter (`:339-349`).
5. **Keep a single wording table (`UiText`) and the banned-terms registry test** for anything user-facing; route raw Core/engineer messages exclusively to the Operation Log tail (Show details / Save details).
6. **Use plain headless tests for invariants; use real-render headless (`UseSkia` + `UseHeadlessDrawing=false`) only as a temporary step when you need actual screenshots** during visual inspection, and revert it (`a19f876`) so CI stays deterministic.
7. **Keep merge-state claims in the tracked doc** (`docs/results.md`), not bare SHAs: it records the deterministic suites green at `development` @ `81e0db1` (`Serpy.Core.Tests` 509, `Serpy.App.Tests` 165, `Serpy.InstallerBootstrapper.Tests` 20) and names the red FlaUI classes explicitly.

## Related Issues

- **Still red, tracked separately (`docs/results.md:25`):** `tests/Serpy.Windows.UiTests` `DashboardComponentTests` and `SetupComponentTests` fail on desktop as of 2026-11-22 (last run at `6690609`):
  - `DashboardComponentTests` predates U10's collapsed `DetailsExpander` layout (`BuildButton`/`InitializeButton` now live inside `DetailsExpander`).
  - `SetupComponentTests` expects `WelcomeScreen` on a clean launch, exposing an unfixed first-run activation gap in `src/Serpy.App/App.axaml.cs` (`HandlePostStatusUpdate` at `:106-143` never calls `Setup.Activate()` or `DashboardViewModel.ApplySetupStatus` when `RecommendedRoute == LaunchRoute.Setup`, even though `ApplySetupStatus` exists at `src/Serpy.App/ViewModels/DashboardViewModel.cs:334` and plan U10 specifies showing the setup view on unfinished setup).
- `docs/solutions/ui-bugs/avalonia-uia-com-interop-missing-controls.md` — same Windows UI-automation area: FlaUI cannot see the Avalonia tree unless COM interop is enabled; relevant when the `UiTests` suite is brought back to green on the self-hosted WHPX runner.
- Operation Log contract (Show details tails it, Save details includes it) is the durable destination for every raw diagnostic that `ApplyFailureText` now diverts off the default screens.
