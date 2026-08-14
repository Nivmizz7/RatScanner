# Agent Backlog

Working notes for issues flagged by AI agents, not user-requested items. (User-requested items stay in `BACKLOG.md`.)

Items are grouped by impact. The top group contains concrete correctness/reliability problems with reachable failure modes. The lower group is cleanup or design questions that need human confirmation before acting.

---

## P0 — Correctness issues (fix before merge)

### Settings page reports `Connected` before activation is verified

**Priority:** P0
**Status:** Done (fixed in `3c9b452`)
**Files:** `src/App/Pages/App/Settings/SettingsTracking.razor`, `src/App/Components/ChangeConnectionDialog.razor`

The settings connection flow and `ChangeConnectionDialog` set the UI state to `TrackerConnectionState.Connected` before/without checking `TarkovTrackerDB.ConnectionState` after `ActivateTrackerModeAsync`. `ActivateTrackerModeAsync` re-validates the token and fetches progress; it can fail due to network or rate limits and set `TarkovTrackerDB.ConnectionState` to `ConnectionError`. The settings card then shows `Connected` while the app attention indicator (which reads the DB) shows an error.

Resolution: both connect paths now read `RatScannerMain.Instance.TarkovTrackerDB.ConnectionState` after `ActivateTrackerModeAsync` and use that for the UI state (`SettingsTracking.razor:391,487,540,542`); `OpenChangeConnectionDialogAsync` derives state from the post-activation DB state.

---

## P1 — Reliability / efficiency issues

### `ChangeConnectionDialog` leaks validation work when closed by Escape/backdrop

**Priority:** P1
**Status:** Done (fixed in `3c9b452`)
**Files:** `src/App/Components/ChangeConnectionDialog.razor`

`ChangeConnectionDialog` keeps a `CancellationTokenSource _validation` for the async token test but does not implement `IDisposable`. If the user dismisses the dialog via Escape or the backdrop while validation is running, the async work continues and `SubmitAsync` eventually calls `DialogInstance.Close(DialogResult.Ok(true))` on a closed/disposed dialog instance. `Cancel()` only runs when the Cancel button is clicked.

Resolution: `ChangeConnectionDialog.razor:4` adds `@implements IDisposable`; `Dispose()` at `:256-261` cancels and disposes `_validation`.

---

## P2 — Cleanup or verify before acting

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
