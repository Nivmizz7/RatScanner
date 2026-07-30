# Project overview

## Purpose

RatScanner is an **external** Escape from Tarkov companion: it screenshots the game client, identifies items via image processing / OCR, and shows flea / trader prices plus quest and hideout relevance when tracking data is available.

It does **not** read game memory or inject into the client.

## Main user workflows

1. **Name scan** — capture inspection UI (marker + name text), OCR, map to catalog item.
2. **Icon scan** — capture stash/inventory icon region (modifier + click), template match icons.
3. **Manual search** — MudAutocomplete item search in the main Blazor UI.
4. **Recent scans** — five deduplicated scan-time snapshots retained for the session and shown on the Scan page.
5. **Overlays** — non-interactive tooltip overlay; interactive search/map overlay via hotkey.
6. **Minimal UI** — compact always-on-top window with configurable fields.
7. **Tracking (optional)** — mode-specific TarkovTracker.org PvP/PvE keys (or the legacy PvP-only TarkovTracker.io key) enable quest/hideout need hints and optional team progress.
8. **Updates** — GitHub Releases on the fork (`GitHubUpdateService`), not upstream updater CDN.

## Supported platform

- **Windows x64 only** (WPF, WinForms tray/screens/DPI helpers, WebView2, x64 OpenCvSharp native runtime, DPAPI config).
- Target TFM and x64 platform target are set in `src/App/RatScanner.csproj` (`net10.0-windows…`). The product does not support x86 or non-Windows hosts.

## High-level stack

| Layer | Technology |
| --- | --- |
| Host | WPF (`PageSwitcher`, tray, jump list) |
| Tray / multi-monitor | WinForms (`NotifyIcon`, `Screen`) — reason both WPF and WinForms are enabled |
| Embedded UI | Blazor Hybrid (`Microsoft.AspNetCore.Components.WebView.Wpf`) + MudBlazor |
| Scan engine | Standalone `tarkovtracker-org/RatEye` submodule at `src/ScanEngine` |
| Item DB for matching | RatStash database built from tarkov.dev item projections |
| OCR | Tesseract (+ traineddata under `Data/`) |
| Vision | OpenCvSharp (ScanEngine) |
| Catalog APIs | json.tarkov.dev bulk JSON; slim maps GraphQL on api.tarkov.dev |
| Progress APIs | TarkovTracker backends via `APIClient` / `TarkovTrackerDB` |
| Serialization | Newtonsoft.Json |

Exact package versions: project files only.

## Project identity

| | Historical upstream | This project |
| --- | --- | --- |
| Org/repo | `RatScanner/RatScanner` | `tarkovtracker-org/RatScanner` |
| Semver | 3.x line | **4.x** |
| Support | Historical | TarkovTracker Discord + this repo |
| Branding | RatScanner | Product name `RatScanner` (`Constants.Branding`); UA `RatScanner/…` |

License: root `LICENSE` (Elastic License 2.0–based). Redistribution must include LICENSE and modification notice (About / README attribution).

## Important product constraints

- External screenshots only; Borderless/Windowed game mode may be required for overlays.
- Name/icon scan have known accuracy limits (shared icons, lighting, base item only for weapons, etc.) — see root `README.md` / `FAQ.md`.
- Cold start should remain usable from offline API cache; maps and optional crafts/barters must not block readiness unnecessarily.
- User-Agent and product strings identify the product (`RatScanner/…`).

## Established non-goals

- Not a memory cheat / ESP / aim aid.
- Not an x86, Linux, or cross-platform client.
- Not a NuGet consumer of RatEye. RatScanner references the checked-out submodule source; RatEye owns its package/version independently.
- Not GraphQL-first bulk catalog (json documents are primary; maps are the intentional slim GraphQL exception).
- Not day-to-day use of `publish.bat` for iteration.
- Not tracking historical upstream version numbers for this project's releases.
