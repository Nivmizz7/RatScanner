# Architecture Analysis — RatScanner App

## Header

- **Analyzed:** 2026-08-07
- **Branch:** `fix/scan-cooldown`
- **Analyzed commit:** `ced3c8a` (`3d8c6f6` analyzer gate + `c00191c` analyzer fixes + `ced3c8a` submodule bump)
- **RatEye submodule:** `24f8806` (v4.0.0-27-g24f8806 — merged upstream PR #2 + RatEyeTest cleanup)
- **Working tree at analysis time:** analyzer gate and fixes committed; unrelated in-progress perf-diagnostics work (`RatScannerMain`, `PageSwitcher`, `src/App/Diagnostics/`, untracked test files) present uncommitted. Evidence below reflects that exact state.
- **Method:** static evidence pass only — no source changes, no refactoring, no new dependencies. Every finding carries `Status` and `Last verified` so this document can serve as a living register instead of a one-time report.

## Dependency map (current)

```text
Input hooks (UserActivityHelper, native) ─┐
GDI+ screen capture (RatScannerMain)       ├─► RatScannerMain (god-singleton)
TarkovDevAPI (static, catalog) ────────────┤      │  owns: RatEyeEngine, TarkovTrackerDB,
RatConfig (static, global config) ─────────┘      │       HotkeyManager, timers, ItemQueue
                                                  ▼
                         MenuVM ◄── shared by ──► WPF MinimalMenu + Blazor/MudBlazor pages
                         SettingsVM ◄── consumed by ──► MudBlazor Settings pages
                         AppStateService ◄─ WPF⇄Blazor bridge ─► PageSwitcher, BlazorOverlay
```

- Projects: `App → RatEye` (source `ProjectReference`), `Tests → App`. `RatEyeTest`/`RatEye.Benchmarks` live in the RatEye repository (not in `RatScanner.sln`).
- **39 direct `RatScannerMain.Instance` call sites** across App (incl. Razor pages); Razor pages also call `TarkovDevAPI.GetItems()` statically.

## Findings

### 1. `RatScannerMain` god-singleton — UI service locator

- **Severity:** High | **Confidence:** High | **Status:** Open | **Last verified:** `ced3c8a`
- **Evidence:** `src/App/RatScannerMain.cs` (987 LOC, 15 `lock` sites, owns `RatEyeEngine`, `TarkovTrackerDB`, `HotkeyManager`, 2 `Timer`s, CTS, `ItemQueue`, scan pipeline, tracker refresh, engine rebuild, UI notification); `MenuVM.cs:15` (`DataSource`), `ItemExtensions.cs:13`, `Pages/App/Settings/SettingsTracking.razor:250`, `Components/ChangeConnectionDialog.razor:177`, `Pages/App/Index.razor:579` all reach the singleton or `TarkovDevAPI` statics directly. Ctor runs on the WPF dispatcher and performs catalog load, hotkey setup, engine setup (`RatScannerMain.cs:105-180`, documented "the bulk of startup, all on this thread").
- **Why it matters:** every subsystem depends on one class; UI→singleton means global state and implicit ordering; ctor cost blocks the UI thread at startup; the orchestration is impossible to unit-test (the existing `ScanPipelineImageHarnessTests` *mirrors* the pipeline precisely because it cannot be invoked).
- **Current flow:** hooks/UI → `Instance` → methods mutate shared fields under heterogeneous locks.
- **Target boundary:** establish an **application-level composition root shared by the WPF host and the Blazor UI**. Blazor's service collection can participate, but the architecture must not make WPF depend on the Blazor container. Extract services behind interfaces (scan orchestrator, tracker, catalog, engine lifecycle) and inject them into both UI stacks.
- **Estimated scope:** Large
- **Sequencing:** first — everything else benefits.

### 2. `RatConfig` — process-wide mutable static configuration hub

- **Severity:** High | **Confidence:** High | **Status:** Open | **Last verified:** `ced3c8a`
- **Evidence:** `src/App/RatConfig.cs` (697 LOC, `internal static class`), mutable statics (`Enable`, `EnableAuto`, `Language`, `ConfWarnThreshold`, `CooldownMs`, screen/display values, `NameScan`/`IconScan`/`ToolTip`/`MinimalUi`/`Tracking` groups), static events (`RatConfig.cs:199,204`), mutated from `SettingsVM`, read from the scan hot path (`RatScannerMain.cs:567-580` reads `RatConfig.NameScan.*` per scan).
- **Why it matters:** reads/writes race by design (no locking on most fields); "config changed" is a firehose event; engine rebuild triggers on catalog/config change; hard to snapshot or test.
- **Target boundary:** immutable options snapshot per scan + a small `ConfigStore` with change notifications; migrate `RatConfig` reads to injected services.
- **Estimated scope:** Large
- **Sequencing:** after #1 (or in parallel).

### 3. `TarkovDevAPI` — static 1168-LOC service with static caches and engine-rebuild coupling

- **Severity:** High | **Confidence:** High | **Status:** Open | **Last verified:** `ced3c8a`
- **Evidence:** `TarkovDevAPI.cs` — `public static class`, static `Cache`/`InFlightRequests`/`BackoffUntil` `ConcurrentDictionary`s, static `HttpClient`, static `ItemsCacheUpdated` event; `RatScannerMain.cs:181` subscribes and `OnItemsCacheUpdated → SetupRatEye()` rebuilds the OCR engine mid-run (`RatScannerMain.cs:334-363`).
- **Why it matters:** no interface/DI → tests can only hit pure JSON projections (`TarkovDevJsonApiTests`); a catalog refresh rebuilds the engine under nested locks while scans may be in flight; startup loads the offline cache synchronously on the UI thread (`RatScannerMain.cs:132`).
- **Target boundary:** instance service + `ICatalogService`, cache behind a repository; engine rebuild becomes a versioned `IEngineHost` swap with quiesce.
- **Estimated scope:** Large
- **Sequencing:** with #1.

### 4. Scan pipeline threading model — fire-and-forget, sleep-under-lock, no cancellation

- **Severity:** High | **Confidence:** Medium | **Status:** Open | **Last verified:** `ced3c8a`
- **Evidence:** hook callbacks (installed on UI thread, `HotkeyManager.cs:21`) → `ActiveHotkey.OnKeyUp` → `Task.Run(...)` (`ActiveHotkey.cs:107`) → `RatScannerMain.NameScan`: `Monitor.Enter(NameScanLock)` then `Thread.Sleep(50)` (`RatScannerMain.cs:525,536`) + GDI+ capture + OCR; `NameScanScreen` same under lock; `_scanThrottle` (300 ms) caps entry rate only; no `CancellationToken` into the pipeline; `RefreshOverlay` fires `PropertyChanged` from a `System.Timers.Timer` thread (`RatScannerMain.cs:926-945`), relying on consumers to marshal.
- **Why it matters:** hook events can queue unbounded `Task.Run`s (throttle is best-effort per entry point); the blocking sleep holds the static lock and stalls the other scan type; shutdown can race in-flight scans against engine disposal (mitigated by `_disposed` checks + `ObjectDisposedException` catch, but by convention, not contract).
- **Target boundary:** dedicated scan worker with bounded concurrency (semaphore); move the settle-wait out of the lock; thread a cancellation token from `_lifetimeCancellation`.
- **Estimated scope:** Medium
- **Sequencing:** after #1–3 (needs the extracted orchestrator).

### 5. RatEye types leak into App/UI contracts

- **Severity:** Medium | **Confidence:** High | **Status:** Open | **Last verified:** `ced3c8a`
- **Evidence:** `Scan/ItemScan.cs:27` `public abstract Vector2 GetToolTipPosition()` (RatEye `Vector2`); `Scan/ItemIconScan.cs:13,15` public `Vector2 ItemSize`, `ItemExtraInfo`; `ViewModel/SettingsVM.cs:308` writes `RatEye.Config.LogDebug`; `RatScannerMain.cs:334-341` writes `Config.Path.LogFile`, `TesseractLibSearchPath`, `Config.LogDebug`; `View/MinimalMenu.xaml.cs` imports `RatEye`.
- **Why it matters:** the UI layer cannot compile/test without the engine's value types; engine config mutations leak two-way; a RatEye API change ripples into Blazor markup and settings.
- **Target boundary:** app-level value types (`ScreenPosition`, `ItemSize`, `DetectionResult`) mapped at the boundary; engine config written only via a narrow `IEngineConfigurator`.
- **Estimated scope:** Medium
- **Sequencing:** before #4 (defines the boundary the scan worker runs behind).

### 6. Duplication — scan classes, market-data derivation, WPF-vs-Blazor rendering, wiring

- **Severity:** Medium | **Confidence:** High | **Status:** Open | **Last verified:** `ced3c8a`
- **Evidence:** (a) `ItemNameScan` vs `ItemIconScan` are ~70% identical (`diff` confirms parallel ctors/fields); (b) market-data derivation triplicated: `MenuVM` properties (`FleaPrice`, `BestTraderOffer`, `PricePerSlot`…, `MenuVM.cs:76-105`), `ItemExtensions` (`GetBestTraderOffer`, `GetAvg24hMarketPricePerSlot`, `GetTaskRemaining`), and `ScanResultAdapter` → `RecommendationSelector` (`Presentation/ScanResultAdapter.cs:98`); (c) the same scan result rendered in two stacks: WPF `MinimalMenu.xaml.cs` (TextBlocks bound to `MenuVM`) and Blazor `Index.razor`/`Overlay/Index.razor`; (d) manual `MenuVM.PropertyChanged` subscription + `StateHasChanged` wiring duplicated in `Pages/App/Index.razor:520-557` and `Pages/Overlay/Index.razor:126-137`.
- **Why it matters:** fixes and formatting drift between parallel implementations; two rendering stacks double the UI maintenance surface.
- **Target boundary:** one derived-result model (already exists: `ScanResultViewModel`) consumed by both stacks; decide the minimal-UI stack once.
- **Estimated scope:** Medium
- **Sequencing:** after #5 (the result model is the boundary type).

### 7. UI→static/service-locator calls in Razor and extensions

- **Severity:** Medium | **Confidence:** High | **Status:** Open | **Last verified:** `ced3c8a`
- **Evidence:** 39 `RatScannerMain.Instance` sites; `Pages/App/Index.razor:579` `TarkovDevAPI.GetItems()`; `ItemExtensions.cs:13`; `SettingsTracking.razor:250,368,403`; `ChangeConnectionDialog.razor:177,241`; `SettingsAdvanced.razor:200`.
- **Why it matters:** pages cannot be rendered or tested in isolation; hidden ordering dependencies (the singleton must exist before first render).
- **Target boundary:** inject the extracted services (the DI container already exists).
- **Estimated scope:** Small–Medium
- **Sequencing:** alongside #1 (mechanical).

### 8. File-level complexity hotspots

- **Severity:** Medium | **Confidence:** High | **Status:** Open | **Last verified:** `ced3c8a`
- **Evidence:** `PageSwitcher.xaml.cs` 1166 (window chrome, tray, minimal-UI geometry, content-fit animation state machine), `TarkovDevAPI.cs` 1168, `RatScannerMain.cs` 987, `UserActivityHelper.cs` 741 (hooks + Win32 enums), `SettingsVM.cs` 718, `RatConfig.cs` 696, `Pages/App/Index.razor` 763, `Settings/SettingsTracking.razor` 725, `Shared/ScannerStatus.razor` 342.
- **Why it matters:** review friction, merge conflicts, high defect-density zones; single-responsibility violations (`PageSwitcher` alone has four concerns).
- **Target boundary:** split by concern (e.g. `WindowChromeManager`, `TrayIconController`, `ContentFitController`); extract Razor partials/components.
- **Estimated scope:** Medium
- **Sequencing:** ongoing, after #1–7 lock the boundaries.

### 9. Static service-class family and the fatal `Logger`

- **Severity:** Medium | **Confidence:** Medium | **Status:** Open | **Last verified:** `ced3c8a`
- **Evidence:** `Logger` (static queue + `Interlocked` single-flight `Task.Run`, `_crashed` latch, `LogError` is process-fatal — shows FAQ dialog, `Logger.cs:44-60`); `GitHubUpdateService` (static, writes PowerShell apply script that kills the app, `GitHubUpdateService.cs:436-439`); `WebView2PowerSaver`/`WebView2DpiWorkaround` (static).
- **Why it matters:** global side effects (process death on `LogError`) are implicit; static state makes shutdown and test isolation hard.
- **Target boundary:** instance-ize where testability matters (Logger via a sink interface); keep fatal-logging explicit and documented.
- **Estimated scope:** Medium
- **Sequencing:** after #1–3.

### 10. Native hook layer exposes WPF Input types

- **Severity:** Low | **Confidence:** High | **Status:** Open | **Last verified:** `ced3c8a`
- **Evidence:** `UserActivityHelper` (static native hooks) raises `KeyUpEventArgs` carrying WPF `Key`/`MouseButton` (`Hotkey.cs:9-19`); the native→input abstraction boundary is expressed in UI-framework types.
- **Why it matters:** the hook layer cannot be reused or tested outside a WPF process; framework coupling hides where the native boundary really is.
- **Target boundary:** framework-neutral input value types (`InputKey`, `InputButton`, `InputDevice`) in the hook layer; WPF mapping at the edge.
- **Estimated scope:** Small
- **Sequencing:** with #9 (same family of static/low-level services).

### 11. `TarkovTrackerDB` single-lock design and tracking refresh

- **Severity:** Low–Medium | **Confidence:** Medium | **Status:** Open | **Last verified:** `ced3c8a`
- **Evidence:** 23 `lock (_stateLock)` sites (`TarkovTrackerDB.cs`), CTS swapped on config change (`TarkovTrackerDB.cs:158-171`), refresh timer in `RatScannerMain`.
- **Why it matters:** one lock serializes validation, refresh, and config swaps; generation-based state is correct today but hard to extend.
- **Target boundary:** split state machine from transport; per-concern locks or an actor pattern.
- **Estimated scope:** Medium
- **Sequencing:** last (correct today, low urgency).

## Not a problem (investigated and cleared)

Preserved as a record of investigated false leads so future agents do not re-escalate them:

- **Event subscription hygiene:** `RatScannerMain` (`:181`/`:957`), `SettingsVM` (`:44`/`Dispose`), `ActiveHotkey` (`:63`/`Dispose`), `PageSwitcher` (`OnClosed`), `BlazorOverlay` (`:34`/`:272`), both Razor pages — **all unsubscribe**.
- **Dispose ordering:** `App.OnExit` isolates each disposal (`App.xaml.cs:225-249`); `RatScannerMain.Dispose` is orderly (unsubscribe → cancel → timers → hotkeys → DB → engine under nested locks → CTS).
- **Engine rebuild:** previous `RatEyeEngine` disposed under the documented lock order (`NameScanLock` = 0, `IconScanLock` = 1); the lock order is consistent everywhere.
- **`TarkovTrackerDB` CTS swap** disposes the old CTS before replacing it.
- **No `async void` in .cs files**; the single occurrence in `Index.razor:531` is a UI event handler feeding `StateHasChanged` (noted in finding #8's file, not a crash vector — global exception handlers exist).
- **ScanThrottle** caps hotkey-driven scan entry; **TarkovDevAPI** has request dedup (`Lazy<Task>`), backoff, and offline cache — resilient by design.
- **`SettingsPersistenceService`** serializes saves with a lifetime CTS — safe.
- **Test culture is real:** 24 test files cover pure logic (quest classifier, presentation, persistence, config migration, window bounds, JSON projection, throttling); reliability tests use fake `HttpMessageHandler`s (no network); `ScanPipelineImageHarnessTests` + `RatEyeTest` (69 tests) mirror the pipeline with real fixtures. The harness *mirroring* is itself evidence for finding #1.
- **Native interop items fixed this cycle** (blur HRESULT, P/Invoke marshaling, tray disposal, RatEye statics) — verified clean.
- **Analyzer gate / Fallow false positives / wpftmp double-build** — resolved; tooling, not architecture.

## Ranked Top 10

| # | Finding | Severity | Scope | Depends on |
| --- | --- | --- | --- | --- |
| 1 | `RatScannerMain` god-singleton → composition root + service extraction | High | Large | — |
| 2 | `RatConfig` static config hub → snapshots + `ConfigStore` | High | Large | 1 |
| 3 | `TarkovDevAPI` static service + engine-rebuild coupling | High | Large | 1 |
| 4 | Scan threading: bounded worker, no sleep-under-lock, cancellation | High | Medium | 1–3 |
| 5 | RatEye types in UI contracts → boundary value types | Med-High | Medium | 1 |
| 6 | Duplication: scan classes, derivation triplication, dual render stacks | Medium | Medium | 5 |
| 7 | UI→singleton/static direct calls → DI | Medium | Small | 1 |
| 8 | Complexity hotspots (6 files >700 LOC) → split by concern | Medium | Medium | 1–7 |
| 9 | Static service family + fatal `Logger` contract | Medium | Medium | 1–3 |
| 10 | `TarkovTrackerDB` single-lock design | Low-Med | Medium | 1 |

Registered but below the ranked cut: finding #10 (hook-layer WPF types, Low severity) — fold into the #9 work when the static service family is touched.

**Sequencing:** 1→2→3 (spine: composition root, snapshot config, instance catalog) → 5 (boundary types) → 4 (threading on top of the clean boundary) → 7 (mechanical DI) → 6 → 8 → 9 → 10. Items 1–3 are the enabling set.

## Recommended first tranche (implementation guidance)

Do **not** start by splitting the 987-line class — a god class split without interfaces tends to become five tightly coupled classes with the same service-locator architecture. Define ownership boundaries first, then move one responsibility at a time while keeping behavior unchanged:

1. Establish the application-level composition root (shared by the WPF host and Blazor UI; WPF must not depend on the Blazor container).
2. Define `IScanOrchestrator` and the scan-facing contract.
3. Replace a small set of `RatScannerMain.Instance` UI call sites with injection.
4. Add characterization tests around the moved surface.
5. Stop and reassess dependency direction.

Only after the pattern is proven, tackle configuration, catalog services, and engine lifecycle (findings 2–3).

## Maintenance rules for this register

- Update `Status` and `Last verified` when a finding changes; do not delete findings — supersede them.
- Re-check the "Not a problem" section before filing anything that resembles a cleared lead.
- Re-run the analysis pass only when the dependency map or a finding's evidence is stale (new subsystems, boundary moves).
