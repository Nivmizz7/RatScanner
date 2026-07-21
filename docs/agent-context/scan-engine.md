# Scan engine

Scoped mandatory rules: `src/ScanEngine/AGENTS.md`. Provenance: `src/ScanEngine/VENDOR.md`.

## Role

The scan engine (folder `src/ScanEngine/`, assembly and namespaces still **`RatEye`**) turns screenshots into item matches using:

- **Inspection / multi-inspection** — name-plate OCR path (Tesseract).
- **Inventory + icon template matching** — icon scan path (OpenCvSharp), optional rotation.
- **IconManager** — static icon library from `Data/icons` (+ optional dynamic cache config).

The App owns capture, hotkeys, mapping to tarkov.dev catalog models, and UI. The engine owns image processing.

## How App wires the engine

`RatScannerMain.SetupRatEye()`:

1. Builds a RatStash `Database` from cached tarkov.dev items (`RatStashDatabaseFromTarkovDev`). Empty DB if cache cold.
2. Constructs `RatEyeEngine` with `GetRatEyeConfig()` (paths, scale from resolution, language, icon scan modes).
3. Swaps engine under name/icon locks; disposes previous instance.
4. Rebuilds when items cache updates or settings that affect processing change.

Scan entrypoints (`NameScan`, `IconScan`, `NameScanScreen`) call `RatEyeEngine.NewInspection` / inventory icon processing, then wrap results in `ItemNameScan` / `ItemIconScan`.

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

Native OpenCV bits come from OpenCvSharp Windows packages on ScanEngine. Missing Data degrades or breaks scanning; that is a setup problem, not a reason to restore NuGet RatEye.

## Confidence and presentation handoff

Engine results surface through App scan types (`ItemNameScan` / `ItemIconScan`) into `ItemQueue` → `MenuVM` / `Presentation/*` for tooltip and main UI. Confidence warning thresholds live on `RatConfig.NameScan` / `IconScan`.

## Fixture and accuracy expectations

- Upstream RatEye test binaries are **not** vendored (size). See `VENDOR.md`.
- App unit tests cover limited cache/presentation contracts plus OpenCV native/pipeline smoke (`OpenCvPipelineTests` uses **synthetic** bitmaps only — not game assets) and icon-cache regeneration.
- Accuracy-sensitive changes (OCR name match, icon template thresholds) still require manual scan smoke with real Data (+ game or carefully prepared captures). Do not treat synthetic OpenCV tests as accuracy proof.
- Prefer deterministic pure helpers when adding new regression coverage.
- Keep OpenCvSharp Extensions + Windows packages on the **same** version; do not adopt slim runtimes unless every used module is present.

## Vendoring rules

- Sources vendored from historical RatEye tag noted in `VENDOR.md`.
- Referenced only via `ProjectReference` from App.
- **Never** add NuGet package `RatEye` back to the solution.
- Do not publish this project as a NuGet package from the monorepo (`IsPackable=false`).
- Namespaces stay `RatEye` unless a deliberate, coordinated rename is requested.

## Changing processing behavior

1. Read this file + nested ScanEngine `AGENTS.md` + relevant Processing types.
2. Prefer minimal, testable changes inside ScanEngine when the logic is pure image matching.
3. Keep App ↔ engine contract stable (Config shape, Item ids via RatStash).
4. Rebuild App after engine changes (ProjectReference).
5. Validate with build/tests; manual scan smoke for accuracy-sensitive edits.
6. Update `VENDOR.md` only when provenance or license facts change — not for every code tweak.

## What not to put in ScanEngine

- HTTP / tarkov.dev clients
- WPF / Blazor / MudBlazor
- TarkovTracker progress
- Application settings persistence

Keep the engine library-shaped even though it lives in this repo.
