# Agent Backlog

Working notes for issues flagged by AI agents, not user-requested items. (User-requested items stay in `BACKLOG.md`.)

Items are grouped by impact. The top group contains concrete correctness/reliability problems with reachable failure modes. The lower group is cleanup or design questions that need human confirmation before acting.

---

## P0 — Correctness issues (fix before merge)

### PvP source save failure is silently overwritten/ignored

**Priority:** P0
**Status:** Done (fixed in `3c9b452`)
**Files:** `src/App/Pages/App/Settings/SettingsTracking.razor`, `src/App/Components/ChangeConnectionDialog.razor`

When a new org/io token validates successfully, the code then saves `PvpSource` to match the new provider. If `SettingsVM.SetTarkovTrackerPvpSourceAsync` fails, the token is persisted but the source is not. The UI then reports `Connected` and clears the error, so the persisted configuration can disagree with what the user sees.

Resolution: failed source saves now set `_orgError[mode]` / `_ioError` / `_error` to `CredentialSaveFailed` and leave the state on the prior value (`SettingsTracking.razor:371,384,467,480`; `ChangeConnectionDialog.razor:188,209,216`); the dialog no longer auto-closes on failure.

### Settings page reports `Connected` before activation is verified

**Priority:** P0
**Status:** Done (fixed in `3c9b452`)
**Files:** `src/App/Pages/App/Settings/SettingsTracking.razor`, `src/App/Components/ChangeConnectionDialog.razor`

`ConnectOrgAsync`/`ConnectIoAsync` and `ChangeConnectionDialog` set the UI state to `TrackerConnectionState.Connected` before/without checking `TarkovTrackerDB.ConnectionState` after `ActivateTrackerModeAsync`. `ActivateTrackerModeAsync` re-validates the token and fetches progress; it can fail due to network or rate limits and set `TarkovTrackerDB.ConnectionState` to `ConnectionError`. The settings card then shows `Connected` while the app attention indicator (which reads the DB) shows an error.

Resolution: both connect paths now read `RatScannerMain.Instance.TarkovTrackerDB.ConnectionState` after `ActivateTrackerModeAsync` and use that for the UI state (`SettingsTracking.razor:391,487,540,542`); `OpenChangeConnectionDialogAsync` derives state from the post-activation DB state.

---

## P1 — Reliability / efficiency issues

### `ChangeConnectionDialog` leaks validation work when closed by Escape/backdrop

**Priority:** P1
**Status:** Done (fixed in `3c9b452`)
**Files:** `src/App/Components/ChangeConnectionDialog.razor`

`ChangeConnectionDialog` keeps a `CancellationTokenSource _validation` for the async token test but does not implement `IDisposable`. If the user dismisses the dialog via Escape or the backdrop while validation is running, the async work continues and `SubmitAsync` eventually calls `DialogInstance.Close(DialogResult.Ok(true))` on a closed/disposed dialog instance. `Cancel()` only runs when the Cancel button is clicked.

Resolution: `ChangeConnectionDialog.razor:4` adds `@implements IDisposable`; `Dispose()` at `:256-261` cancels and disposes `_validation`.

### Double `ActivateTrackerModeAsync` when switching PvP source

**Priority:** P1
**Status:** Done (fixed in `3c9b452`)
**Files:** `src/App/ViewModel/SettingsVM.cs`, `src/App/Pages/App/Settings/SettingsTracking.razor`, `src/App/Components/ChangeConnectionDialog.razor`

`SetTarkovTrackerPvpSourceAsync` fires `ActivateTrackerModeAsync` in its `applyRuntime` action (fire-and-forget). Every caller (`ConnectOrgAsync`, `ConnectIoAsync`, `ChangeConnectionDialog.SubmitAsync`) then awaits its own `ActivateTrackerModeAsync`. `TarkovTrackerDB.Configure` does not cancel an existing `InitAsync` when the same token/endpoint/mode is already configured, so two overlapping `InitAsync` calls can run, each validating and setting `ConnectionState`.

Resolution: `SettingsVM.SetTarkovTrackerPvpSourceAsync` (`:347-354`) no longer passes an `applyRuntime`; callers own activation.

---

## P2 — Cleanup or verify before acting

### Verify `PvpSource` fallback vs `IsModeConfigured` consistency

**Priority:** P2
**Status:** Not started
**Files:** `src/App/RatScannerMain.cs`, `src/App/Pages/App/Settings/SettingsTracking.razor`

`GetActiveTrackerConfiguration` falls back to the org PvP token when `PvpSource == Io` but `IoToken` is empty (`RatScannerMain.cs:283-290`). `IsModeConfigured` in `SettingsTracking.razor:277` uses `HasActivePvpToken`, which checks the source-specific token. The two can disagree: tracking may be active via the org fallback while the settings UI says the mode is not configured. `RemoveIoKeyAsync` also leaves `PvpSource == Io` after clearing the Io token.

This may be intentional — the comment says "so a misconfigured source never silently disables tracking" — but it creates a confusing UI. Verify whether the fallback should also update `PvpSource` or whether the UI should reflect fallback state.

Fix shape options:

1. If `PvpSource == Io` and `IoToken` is empty, set `PvpSource = Org` when an org token exists.
2. Make `IsModeConfigured` aware of the fallback.
3. Remove the fallback and let the user explicitly choose the source.

### Remove stale i18n keys

**Priority:** P2
**Status:** Done
**Files:** `src/App/i18n/en.json`, `src/App/i18n/es.json`, `src/App/i18n/fr.json`, `src/App/i18n/pl.json`, `src/App/i18n/pt.json`, `src/App/i18n/ru.json`, `src/App/i18n/zh.json`

Several keys were no longer referenced in code. They increased translation/maintenance load.

Removed from all locale files: `BackendLabel`, `BackendOrgOption`, `BackendIoOption`, `BackendIoWarning`, `BackendIoWarningLink` (old backend UI), `InvalidToken`, and `NoTraderComparison` (no longer emitted by `Index.razor`). `TrackerOverflowMenu` was kept — it is now used as the `MudMenu AriaLabel` at `SettingsTracking.razor:64`.

### Dead `RatConfig` properties

**Priority:** P2
**Status:** Done
**Files:** `src/App/RatConfig.cs`

`RatConfig.Tracking.TarkovTracker.ActiveOrgToken`, `OrgEnabledForActiveMode`, and `IoEnabledForActiveMode` were defined but never referenced.

Removed: all three properties deleted from `RatConfig.cs`.

### Verify CSS isolation for `source-cards` / `MudRadioGroup`

**Priority:** P2
**Status:** Not started
**Files:** `src/App/Pages/App/Settings/SettingsTracking.razor`, `src/App/Pages/App/Settings/SettingsTracking.razor.css`, `src/App/Components/ChangeConnectionDialog.razor`, `src/App/Components/ChangeConnectionDialog.razor.css`

Both `SettingsTracking` and `ChangeConnectionDialog` apply the `source-cards` class directly to `MudRadioGroup`. In Blazor CSS isolation the `source-cards` selector is scoped to the parent component, so it may not match the rendered `MudRadioGroup` root (which lives in the child component's render tree). The `::deep .mud-radio-group` child selectors may therefore be dead. This only matters if MudBlazor's default `MudRadioGroup` layout does not already produce the desired card spacing.

This needs a WebView smoke test to confirm; if the cards render correctly without the custom layout, the `source-cards` styles can be simplified or removed.

---

## Template for new items

```text
### Short title

**Priority:** P0 | P1 | P2
**Status:** Not started | In progress | Blocked | Done
**Files:** path/to/file, ...

Describe the problem with evidence (file:line, observed behavior, expected behavior).
Note whether the item is a concrete correctness issue or a design/cleanup question.
Keep it scoped so it can be picked up without re-investigation.
```
