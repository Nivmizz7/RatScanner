# Data integrations

## Overview

| System | Client | Role |
| --- | --- | --- |
| Catalog bulk (items, tasks, hideout, crafts, barters) | `TarkovDevAPI` → **json.tarkov.dev** | Primary market/quest/hideout data |
| Maps (id/name/normalizedName) | `TarkovDevAPI` slim **GraphQL** on api.tarkov.dev | Avoid multi-MB Regular/PvE maps JSON on critical path |
| Maps fallback | json.tarkov.dev maps stream extract | Seasonal (unsupported GraphQL enum), or when GraphQL fails/empty |
| RatScannerData runtime bundle | `scripts/setup-data.ps1` + `scripts/RatScannerData.ps1` | Pinned icon templates, OCR models, maps, fallback image, and provenance manifest |
| Interactive map tiles/meta | local `Data/maps.json` via `MapDataLoader` | Overlay map viewer |
| Progress tracking | `TarkovTrackerDB` + `APIClient` | Quests/hideout/team |
| App updates | `GitHubUpdateService` | Fork releases only |

User-Agent for APIs: `RatScanner/{version}` (and GitHub-specific UA).

**Important correction vs older prose:** bulk catalog is **not** GraphQL-first. Maps intentionally use a slim GraphQL query with JSON fallback. Do not reintroduce GraphQL schema generation for items/tasks/hideout.

## json.tarkov.dev (bulk catalog)

Authoritative entry: `src/App/TarkovDevAPI.cs`.

- Base: `https://json.tarkov.dev`
- Paths are game-mode + document, e.g. `{regular|pve|pvp-season}/items`, locale bundles `{mode}/items_{locale}`, same pattern for tasks/hideout/maps. RatScanner's internal `Seasonal` mode maps to `pvp-season`.
- Prices ride with the items document (medium TTL).
- Domain models: `src/App/TarkovDev/*` (app-facing projections, not raw GraphQL types).
- **Do not** reintroduce GraphQL schema generators for bulk catalog.
- **Do not** bypass `TarkovDevAPI` with raw HttpClient for these documents (lose rate limit, dedup, offline cache, backoff).

### Caching model

- In-memory concurrent cache keyed by query identity (locale + game mode where relevant).
- Offline persistence: `RatConfig.WriteToCache` / `ReadFromCache` under `%TEMP%\RatScanner\Cache\` (SHA-256 hashed filenames). Freshness uses **file last-write time** vs configured TTLs (`SuperShortTTL`, `ShortTTL`, `MediumTTL`, `LongTTL` on `RatConfig`).
- Startup: `TryInitializeCacheFromOffline()` then optional background `InitializeCache()`.
- In-flight request coalescing (`InFlightRequests`) and 429 handling with Retry-After / backoff live in the client.
- HTTP client enables gzip/deflate/brotli decompression; 60s timeout.

Items cache update raises `ItemsCacheUpdated` → App rebuilds RatEye item database.

### Error / degraded mode

- Offline projected cache allows startup without network for previously warmed documents.
- Failed live fetches leave last-good memory/disk data when present.
- Maps may be empty/not-ready without blocking items/tasks/hideout cold start.

## RatScannerData runtime bundle

RatScanner does not fetch recognition templates at runtime. Development setup and release packaging install the content-addressed release pinned in `scripts/RatScannerData.ps1` from `tarkovtracker-org/RatScannerData`.

The installer verifies three assets from the same release:

1. `Data.zip` hashes to both the release's `Data.zip.sha256` value and the repository-pinned digest.
2. The standalone `manifest.json` is byte-identical to the manifest embedded in the archive.
3. The schema, catalog/skipped/icon counts, required files, and actual extracted icon count are consistent before the destination is replaced.

The bundle's icons are named by tarkov.dev item ID and use RatEye's 63-pixel slot geometry. The manifest records source URLs and skipped generic-placeholder items. When advancing the pin, validate the new RatScannerData release first, update its tag and archive hash together, rebuild the RatScanner artifact, and perform current-EFT fixture/manual scan checks. Package integrity does not by itself prove recognition accuracy.

## Maps: slim GraphQL + fallback

Intentional dual path (documented on `TarkovDevAPI`):

1. Prefer slim GraphQL query selecting only `id`, `name`, `normalizedName` on `https://api.tarkov.dev/graphql` for Regular and PvE.
2. Seasonal skips GraphQL because its `GameMode` enum does not support `pvp-season`; fetch the seasonal JSON documents directly.
3. For Seasonal, or when GraphQL fails/returns empty, extract the maps dictionary from json.tarkov.dev without materializing unrelated multi-MB siblings (`ExtractMapsDictionary` — unit-tested).
4. Maps stay **off cold-start critical path** (background queue + offline projected cache).

`MapDataLoader` combines local interactive `maps.json` with the live catalog ids; empty catalog means “not ready yet” (retryable), not permanent failure.

## Crafts / barters / acquisition hints

- `/crafts` and `/barters` JSON endpoints are fetched and indexed via helpers on `TarkovDevAPI`.
- Presentation layer can surface craft/barter/FIR need hints (`Presentation/ResultPresentationServices` and related).
- Keep heavy recommendation work off blocking cold start unless product requirements change and docs update.

## TarkovTracker

- Config: `RatConfig.Tracking.TarkovTracker` stores independent DPAPI-protected TarkovTracker.org PvP, PvE, and Seasonal PvP keys. TarkovTracker.io is retired and unsupported; config migration removes its obsolete credential and source fields.
- `TarkovTrackerDB` holds only the active mode's progress while retaining separate in-memory last-good snapshots by mode. Configuration generation and cancellation prevent stale PvP/PvE/Seasonal responses from crossing modes.
- API keys are validated explicitly against `/token`: RatScanner requires `GP` (progress read), uses `TP` only when team display is enabled and available, and does not require `WP` because the app does not write progress.
- Periodic refresh (`RefreshProgressAsync`, every 30 min) skips the redundant `/token` call and only fetches `/progress` (or `/team/progress`). If the progress call rejects the key, `_token` is cleared and the next refresh falls back to a full `/token` + `/progress` cycle to correct the connection state. Explicit user actions (mode switch, settings save, key validation) always use the full `InitAsync` flow.
- `.org` token metadata (`gameMode`, with token-prefix fallback for responses that omit it) must match the intended slot before the key is saved: `pvp`/`PVP_`, `pve`/`PVE_`, or `seasonal`/`SZN_`.
- `APIClient` performs bearer GETs with the shared UA, cancellation, and distinct unauthorized / forbidden permission / rate-limit / service failure mapping.
- Models: `FetchModels/TarkovTracker/*`.
- Progress payload facts the UI relies on: `/progress` exposes task/objective completion (including `failed` / `invalid` flags), `playerLevel`, `pmcFaction`, and `gameEdition`. It does **not** expose trader standing, Scav karma, or task-completion timestamps, so reputation-gated and timed tasks can never be classified as definitely active.

### Quest requirement classification

`QuestNeedClassifier` (`src/App/QuestNeedClassifier.cs`) is the single place that turns task gates + tracker progress into per-item need buckets: active (started), available (unstarted but unlocked), future (level/prerequisite locked), conditional (trader-standing / faction-unknown / unmodeled gates), plus counts for kappa and weapon hand-ins. Buckets must never be merged into one "needed" number; conditional needs must never present as active. Task gate fields (level, prerequisites, trader requirements, faction) come from json.tarkov.dev tasks and are projected in `TarkovDev/Models.cs`; the offline tasks cache key is versioned (`tasks_v2_…`) — bump it when the projected task shape changes.

Never log API keys. A failed replacement leaves the previously stored key untouched.

## Locale data (API items, not UI i18n)

Name-scan language (`RatConfig.NameScan.Language` / RatStash language) maps to json.tarkov.dev locale suffixes (`items_en`, `tasks_ru`, …) via extension helpers (see `Extensions.cs`). This is **independent** of UI language files under `i18n/`.

## Domain-model locations

| Area | Path |
| --- | --- |
| App catalog projections | `src/App/TarkovDev/Models.cs` |
| JSON API raw shapes | `src/App/TarkovDev/JsonApiModels.cs` |
| Craft/barter acquisition helpers | `src/App/TarkovDev/Acquisition.cs` |
| Tracker DTOs | `src/App/FetchModels/TarkovTracker/*` |

## Rules for agents

1. Extend catalog access through `TarkovDevAPI` patterns already present.
2. Preserve offline-first startup and maps laziness.
3. Prefer projected app models in cache (client already serializes projected results).
4. When changing endpoints or TTLs, update tests and this file.
5. Never commit API cache files or tokens.
