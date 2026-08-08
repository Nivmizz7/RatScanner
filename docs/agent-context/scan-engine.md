# Scan engine

RatEye's scoped mandatory rules live in the `src/ScanEngine/AGENTS.md` submodule file.

## Role

The standalone RatEye submodule (`src/ScanEngine/`, project under `RatEye/`, assembly and namespaces **`RatEye`**) turns screenshots into item matches using:

- **Inspection / multi-inspection** — name-plate OCR path (Tesseract).
- **Inventory + icon template matching** — icon scan path (OpenCvSharp), optional rotation.
- **Low-confidence icon verification** — exact short-name OCR when traineddata
  is available; ambiguous or duplicate names never override the template.
- **IconManager** — static icon library from `Data/icons` (+ optional dynamic cache config).

The App owns capture, hotkeys, mapping to tarkov.dev catalog models, and UI. The engine owns image processing.

## How App wires the engine

`RatScannerMain.SetupRatEye()`:

1. Builds a RatStash `Database` from cached tarkov.dev items (`RatStashDatabaseFromTarkovDev`). Empty DB if cache cold.
2. Constructs `RatEyeEngine` with `GetRatEyeConfig()` (paths, scale from resolution, language, icon scan modes).
3. Swaps engine under name/icon locks; disposes previous instance.
4. Rebuilds when items cache updates or settings that affect processing change.

Scan entrypoints (`NameScan`, `IconScan`, `NameScanScreen`) call `RatEyeEngine.NewInspection` / inventory icon processing, then wrap results in `ItemNameScan` / `ItemIconScan`. `NameScan` and `IconScan` are rate-limited by a shared `ScanThrottle` (`RatConfig.NameScan.CooldownMs`, default 300 ms and loaded once at startup) using a monotonic clock, so hotkey spam or wall-clock corrections cannot disrupt the OCR pipeline and overlay compositor; `NameScanScreen` is exempt because it is opt-in and already debounced by the 500 ms auto-scan click window.

Lock order (App): name scan (0) then icon scan (1).

## Key engine surfaces

| Type | Purpose |
| --- | --- |
| `RatEyeEngine` | Factory for processing objects; dispose frees Tesseract/OpenCV resources |
| `Config` / `Config.Path` / `Config.Processing` | Scale, language, cache flags, icon modes |
| `Processing.Inspection` | Single inspection OCR match |
| `Processing.MultiInspection` | Multi-name regions |
| `Processing.Inventory` / `Icon` | Grid + icon match |
| `IconManager` | Icon index / templates |

Do not modify config objects after they are passed into an engine instance (engine remarks).

## Screenshot acquisition vs processing

| Stage | Owner |
| --- | --- |
| Region geometry / game display scale | App (`RatConfig`, `Display/*`) |
| Bitmap capture | App `RatScannerMain.GetScreenshot` |
| Preprocess / OCR / template match | ScanEngine |
| Confidence thresholds / warn UI | App config + presentation |
| Catalog mapping / queues / overlays | App |

## Data dependencies

From App output `Data/` (installed via setup-data):

- Static icons directory
- Tesseract traineddata
- Correlation helpers as used by config paths

Native OpenCV bits come from OpenCvSharp Windows packages on RatEye. Missing Data degrades or breaks scanning; that is a setup problem, not a reason to consume the RatEye NuGet package.

## Confidence and presentation handoff

Engine results surface through App scan types (`ItemNameScan` / `ItemIconScan`) into `ItemQueue` → `MenuVM` / `Presentation/*` for tooltip and main UI. Confidence warning thresholds live on `RatConfig.NameScan` / `IconScan`.

## Fixture and accuracy expectations

- RatEye owns synthetic OpenCV, cache, processing, historical fixture, and replay-benchmark coverage.
- RatScanner keeps `ScanPipelineImageHarnessTests` because it mirrors App-owned data projection and capture/crop geometry.
- Accuracy-sensitive changes require a RatEye benchmark report and, when practical, a manual scan smoke with real Data. Do not treat synthetic OpenCV tests as accuracy proof.
- The App retains only the latest captured scan in memory and exports it from Advanced settings on explicit user action. RatEye owns the versioned replay contract and benchmark runner.
- Prefer deterministic pure helpers when adding new regression coverage.
- Keep OpenCvSharp Extensions + Windows packages on the **same** version; do not adopt slim runtimes unless every used module is present.

## Repository boundary

- `src/ScanEngine` is a Git submodule of `tarkovtracker-org/RatEye`.
- App references `src/ScanEngine/RatEye/RatEye.csproj` directly.
- **Never** add a NuGet package reference for RatEye to RatScanner.
- RatEye remains independently packable and versioned; RatScanner's product version remains App-owned.
- RatEye must never reference RatScanner, WPF, Blazor, HTTP clients, or App settings.
- Commit and validate RatEye first, then update RatScanner's gitlink.

## Changing processing behavior

1. Read this file + RatEye's `AGENTS.md` + relevant Processing types.
2. Prefer minimal, testable changes inside RatEye when the logic is pure image matching.
3. Keep App ↔ engine contract stable (Config shape, Item ids via RatStash).
4. Rebuild App after engine changes (ProjectReference).
5. Validate RatEye independently, then RatScanner; replay fixtures/manual scan smoke for accuracy-sensitive edits.
6. Keep RatEye and RatScanner commits reviewable and preserve the dependency ordering.

## What not to put in ScanEngine

- HTTP / tarkov.dev clients
- WPF / Blazor / MudBlazor
- TarkovTracker progress
- Application settings persistence

Keep the engine library-shaped in its standalone repository.
