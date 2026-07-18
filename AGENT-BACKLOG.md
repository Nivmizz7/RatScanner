# Agent Backlog

Working notes for issues flagged by AI agents, not user-requested items. (User-requested items stay in `BACKLOG.md`.)

Items are grouped by impact. The top group contains concrete correctness/reliability problems with reachable failure modes. The lower group is cleanup or design questions that need human confirmation before acting.

---

## P0 — Correctness issues (fix before merge)

### PvP source save failure is silently overwritten/ignored

**Priority:** P0  
**Status:** Not started  
**Files:** `src/App/Pages/App/Settings/SettingsTracking.razor`, `src/App/Components/ChangeConnectionDialog.razor`

When a new org/io token validates successfully, the code then saves `PvpSource` to match the new provider. If `SettingsVM.SetTarkovTrackerPvpSourceAsync` fails, the token is persisted but the source is not. The UI then reports `Connected` and clears the error, so the persisted configuration can disagree with what the user sees.

Evidence:

- `SettingsTracking.razor:376-383` sets `_orgError[mode] = CredentialSaveFailed` and then `_orgError[mode] = null` two lines later, while `_orgState[mode] = Connected` is already set.
- `SettingsTracking.razor:457-466` does the same for `_ioError`.
- `ChangeConnectionDialog.razor:178-180` and `:193-195` set `_error` and then `DialogInstance.Close(DialogResult.Ok(true))` closes the dialog, so the error is never shown and `OpenChangeConnectionDialogAsync` marks the state `Connected`.

Fix shape: treat a failed source save as a hard failure. Do not set state to `Connected`, do not clear the draft/error, and return early. Optionally roll the token back to the previous value so source and token stay consistent.

### Settings page reports `Connected` before activation is verified

**Priority:** P0  
**Status:** Not started  
**Files:** `src/App/Pages/App/Settings/SettingsTracking.razor`, `src/App/Components/ChangeConnectionDialog.razor`

`ConnectOrgAsync`/`ConnectIoAsync` and `ChangeConnectionDialog` set the UI state to `TrackerConnectionState.Connected` before/without checking `TarkovTrackerDB.ConnectionState` after `ActivateTrackerModeAsync`. `ActivateTrackerModeAsync` re-validates the token and fetches progress; it can fail due to network or rate limits and set `TarkovTrackerDB.ConnectionState` to `ConnectionError`. The settings card then shows `Connected` while the app attention indicator (which reads the DB) shows an error.

Evidence:

- `SettingsTracking.razor:380-385` sets `_orgState[mode] = Connected` then calls `ActivateTrackerModeAsync`.
- `SettingsTracking.razor:463-468` sets `_ioState = Connected` then calls `ActivateTrackerModeAsync`.
- `SettingsTracking.razor:568-575` sets `Connected` after a successful dialog close without inspecting the DB state that `ChangeConnectionDialog` activated.

Fix shape: after `ActivateTrackerModeAsync`, read `RatScannerMain.Instance.TarkovTrackerDB.ConnectionState` and use that for the UI state. For inactive modes (where activation is not called), `Connected` is fine because validation already succeeded.

---

## P1 — Reliability / efficiency issues

### `ChangeConnectionDialog` leaks validation work when closed by Escape/backdrop

**Priority:** P1  
**Status:** Not started  
**Files:** `src/App/Components/ChangeConnectionDialog.razor`

`ChangeConnectionDialog` keeps a `CancellationTokenSource _validation` for the async token test but does not implement `IDisposable`. If the user dismisses the dialog via Escape or the backdrop while validation is running, the async work continues and `SubmitAsync` eventually calls `DialogInstance.Close(DialogResult.Ok(true))` on a closed/disposed dialog instance. `Cancel()` only runs when the Cancel button is clicked.

Evidence: `ChangeConnectionDialog.razor:113` declares `_validation`; `Cancel()` at `:136-141` disposes it, but there is no `Dispose()` override.

Fix shape: implement `IDisposable` and cancel/dispose `_validation` when the component is disposed.

### Double `ActivateTrackerModeAsync` when switching PvP source

**Priority:** P1  
**Status:** Not started  
**Files:** `src/App/ViewModel/SettingsVM.cs`, `src/App/Pages/App/Settings/SettingsTracking.razor`, `src/App/Components/ChangeConnectionDialog.razor`

`SetTarkovTrackerPvpSourceAsync` fires `ActivateTrackerModeAsync` in its `applyRuntime` action (fire-and-forget). Every caller (`ConnectOrgAsync`, `ConnectIoAsync`, `ChangeConnectionDialog.SubmitAsync`) then awaits its own `ActivateTrackerModeAsync`. `TarkovTrackerDB.Configure` does not cancel an existing `InitAsync` when the same token/endpoint/mode is already configured, so two overlapping `InitAsync` calls can run, each validating and setting `ConnectionState`.

Evidence:

- `SettingsVM.cs:354-366` `applyRuntime` starts `ActivateTrackerModeAsync` without awaiting.
- `SettingsTracking.razor:384-385` and `:467-468` and `ChangeConnectionDialog.razor:207-208` call it again.

Fix shape: remove the `applyRuntime` activation from `SetTarkovTrackerPvpSourceAsync` and let the callers own activation, since they already call it explicitly and need to update UI state afterwards.

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
**Status:** Not started  
**Files:** `src/App/i18n/en.json`, `src/App/i18n/es.json`, `src/App/i18n/fr.json`, `src/App/i18n/pl.json`, `src/App/i18n/pt.json`, `src/App/i18n/ru.json`, `src/App/i18n/zh.json`

Several keys are no longer referenced in code, and `TrackerOverflowMenu` was added in this diff but is not used in markup. They increase translation/maintenance load.

Evidence (in `en.json`, mirrored in other locales):

- `BackendLabel`, `BackendOrgOption`, `BackendIoOption`, `BackendIoWarning`, `BackendIoWarningLink` (old backend UI) — `src/App/i18n/en.json:110-114`.
- `InvalidToken` — `src/App/i18n/en.json:117`.
- `TrackerOverflowMenu` — `src/App/i18n/en.json:294` and no markup reference.

Fix shape: delete keys from all locale files, or rewire `TrackerOverflowMenu` if it was intended for the overflow menu aria-label.

### Dead `RatConfig` properties

**Priority:** P2  
**Status:** Not started  
**Files:** `src/App/RatConfig.cs`

`RatConfig.Tracking.TarkovTracker.ActiveOrgToken`, `OrgEnabledForActiveMode`, and `IoEnabledForActiveMode` are defined but never referenced.

Evidence: `src/App/RatConfig.cs:131-134`.

Fix shape: remove them if no future use is planned, or wire them into the UI if they were intended to drive status.

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
