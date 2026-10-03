---
title: Avalonia 11 Win32 UIA accessibility tree missing controls when BuiltInComInteropSupport is disabled
date: 2026-09-18
category: ui-bugs
module: Serpy.App
problem_type: ui_bug
component: frontend
severity: high
symptoms:
  - "FlaUI UIA3 AutomationElement.FindFirstDescendant returns null for all Avalonia controls inside a Window"
  - "UIA element tree dump contains only standard Win32 chrome (TitleBar, SystemMenuBar, Minimize, Maximize, Close)"
  - "FlaUI Capturing.Capture.Element on Window captures overlapped desktop background instead of Avalonia control content"
root_cause: config_error
resolution_type: config_change
tags:
  - avalonia
  - flaui
  - uiautomation
  - accessibility
  - com-interop
  - win32
  - native-aot
framework_version: dotnet 10.0
---

# Avalonia 11 Win32 UIA accessibility tree missing controls when BuiltInComInteropSupport is disabled

## Problem

When writing end-to-end Windows UI Automation tests using FlaUI against the compiled `Serpy.App.exe`, FlaUI successfully located the main `Window` by name (`Serpy`), but failed to discover any inner Avalonia controls (`FindFirstDescendant` returned `null` for `StatusBadge`, `BuildButton`, etc.). A full UI element tree dump revealed only default Win32 window chrome (`TitleBar`, `SystemMenuBar`, `Minimize`, `Maximize`, `Close`), and no Avalonia accessibility nodes.

## Symptoms

- `window.FindFirstDescendant(cf => cf.ByAutomationId("StatusBadge"))` returned `null`.
- Dumping `window.FindAllDescendants()` returned an element count of 6 (all standard Win32 title bar buttons and system menu items; zero Avalonia UI components).
- `uitree.txt` showed:
  ```text
  [Window] ID='' Name='Serpy' (Enabled)
    [TitleBar] ID='TitleBar' Name='' (Enabled)
      [MenuBar] ID='SystemMenuBar' Name='System' (Enabled)
        [MenuItem] ID='' Name='System' (Enabled)
      [Button] ID='Minimize-Restore' Name='Minimize' (Enabled)
      [Button] ID='Maximize-Restore' Name='Maximize' (Enabled)
      [Button] ID='Close' Name='Close' (Enabled)
  ```
- Adding explicit `AutomationProperties.AutomationId` in XAML (`DashboardWindow.axaml`) did not resolve the issue.

## What Didn't Work

1. **Adding explicit XAML automation properties:** Setting `AutomationProperties.AutomationId="StatusBadge"` on TextBlock and Button elements did nothing because the underlying Windows UIAutomation bridge was not providing the control tree.
2. **Focus / activation workarounds:** Calling `window.Focus()`, `SetForegroundWindow`, or simulating mouse hover did not surface the inner elements in FlaUI inspections.

## Solution

In `src/Serpy.App/Serpy.App.csproj`, the project configuration originally included:

```xml
<BuiltInComInteropSupport>false</BuiltInComInteropSupport>
```

In .NET, `<BuiltInComInteropSupport>false</BuiltInComInteropSupport>` disables the built-in COM Callable Wrapper (CCW) subsystem. On Windows, Avalonia 11 implements the UIAutomation server provider (`IRawElementProviderSimple`) over Win32 COM via `WM_GETOBJECT`. When built-in COM interop is disabled, the OS request for accessibility providers fails, causing Windows to fall back to standard Win32 window chrome and hiding all Avalonia visual elements from UI automation clients.

Removing `<BuiltInComInteropSupport>false</BuiltInComInteropSupport>` from `src/Serpy.App/Serpy.App.csproj` restored COM CCW support. Re-publishing and running the FlaUI test immediately yielded the complete 21+ control visual tree (`StatusBadge`, `PrimaryActionButton`, `BuildButton`, `DetailsExpander`, etc.).

Additionally, when interacting with Avalonia windows through FlaUI on Windows:
- Owned dialog windows created via `ShowDialog<T>(owner)` appear as direct children of the owner window in the UIA tree (`_window.FindFirstChild(cf => cf.ByControlType(ControlType.Window))`).
- Modal button invocations should use physical mouse click simulation (`btn.Click()`) rather than `btn.Invoke()`, as Avalonia's `IInvokeProvider` implementation can encounter COM exceptions when opening modal dialogs on the Win32 platform.

## Why This Works

Windows UIAutomation (UIA) relies on COM interfaces (`IRawElementProviderSimple`, `IRawElementProviderFragmentRoot`) returned in response to `WM_GETOBJECT` (with `OBJID_CLIENT`). Avalonia's Win32 platform backend exposes managed accessibility peers through .NET CCWs. With `BuiltInComInteropSupport` enabled (the .NET default), the CLR marshals the Avalonia automation peer as a COM object to the Windows UIA runtime.

## Prevention

1. **Do not disable COM interop in Avalonia desktop apps on Windows:** While `<BuiltInComInteropSupport>false</BuiltInComInteropSupport>` is sometimes recommended in generic Native-AOT guides to avoid trim warnings, Avalonia's Windows desktop accessibility bridge requires it.
2. **Verify UIA element tree in CI:** Automated E2E test suites running on self-hosted Windows runners should dump the visual tree on failure using `DiagnosticCapture.Capture()` to immediately catch missing accessibility providers or tree disconnections.
