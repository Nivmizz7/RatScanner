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

`EngineLifecycleGate` serializes engine construction and owns the publication/stop race. Shutdown marks publication stopped and disposes the currently published engine without waiting for a synchronous build already in progress. If that build later completes, its replacement is disposed instead of being published. This keeps shutdown prompt without requiring RatEye to depend on App lifetime concerns or claiming that a running constructor can be cancelled.

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
| Bitmap capture (GDI, detection-gated HDR via DXGI) | App `RatScannerMain.GetScreenshot` / `Display/HdrScreenCapture` |
| Preprocess / OCR / template match | ScanEngine |
| Confidence thresholds / warn UI | App config + presentation |
| Catalog mapping / queues / overlays | App |

### HDR (advanced color) capture

When a scanned region overlaps a display running in HDR mode, item content can exceed SDR
white and GDI `CopyFromScreen` clips it. The App ships an experimental DXGI Desktop
Duplication path (`Display/HdrScreenCapture` + `Display/HdrToneMapper`, identity-anchored
tone curve over SDR reference white):

- Detection: `Display/HdrScreenCapture.IsHdrCaptureRequired` reads each output's
  `IDXGIOutput6::GetDesc1` color space (HDR10/HLG signal = HDR); per-output states are
  cached for 2 s (per display, so mixed HDR/SDR rigs route each region correctly). The
  DISPLAYCONFIG advanced-color state supplements detection and supplies the display's SDR
  reference white level for tone mapping (the interop structs mirror the native
  `DISPLAYCONFIG_PATH_TARGET_INFO`/`DISPLAYCONFIG_MODE_INFO` records from `wingdi.h`).
- Capture when enabled and HDR: DXGI Desktop Duplication on every intersecting output
  (`IDXGIOutput5::DuplicateOutput1` requesting FP16 scRGB, with 8-bit BGRA fallback), one
  D3D11 device per output session so a duplication failure on one display never affects
  the others; FP16 scRGB frame, region staging via `CopySubresourceRegion`, then tone
  mapping (`Display/HdrToneMapper`) back to an SDR 8-bit BGR bitmap. SDR reference white
  maps 1:1 (the scan pipeline matches SDR reference templates); luminance above white
  saturates at display white.
  Output is `Format24bppRgb`, identical to the GDI path. Any failure returns null and the
  legacy GDI path runs byte-for-byte unchanged. A region spanning an output without a
  session (rotated monitor, failed duplication setup) also returns null — coverage is
  all-or-nothing, never a partially black bitmap.
- **Off by default** (`RatConfig.HdrCapture.Enable`, config-file only). Local measurements
  on an HDR display show the duplication surface delivers SDR content (e.g. a 128-grey
  window reads 150-255) with a boosted, unestablished transfer, while GDI is correct for
  SDR content. Until the duplication transfer is pinned down and validated against a live
  HDR game session, GDI remains the safe default; the DD path is opt-in.

Vortice (`Vortice.Direct3D11` / `Vortice.DXGI`) supplies the DXGI/D3D11 interop for the
HDR path; version pins live in `src/App/RatScanner.csproj`. The engine (RatEye) never sees
HDR data — it receives the same SDR bitmaps as before.

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
