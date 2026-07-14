# Data integrations

## Overview

| System | Client | Role |
| --- | --- | --- |
| Catalog bulk (items, tasks, hideout, crafts, barters) | `TarkovDevAPI` → **json.tarkov.dev** | Primary market/quest/hideout data |
| Maps (id/name/normalizedName) | `TarkovDevAPI` slim **GraphQL** on api.tarkov.dev | Avoid multi-MB maps JSON on critical path |
| Maps fallback | json.tarkov.dev maps stream extract | When GraphQL fails/empty |
| Interactive map tiles/meta | local `Data/maps.json` via `MapDataLoader` | Overlay map viewer |
| Progress tracking | `TarkovTrackerDB` + `APIClient` | Quests/hideout/team |
| App updates | `GitHubUpdateService` | Fork releases only |

User-Agent for fork APIs: `RatScanner-TT/{version}` (and GitHub-specific UA).

**Important correction vs older prose:** bulk catalog is **not** GraphQL-first. Maps intentionally use a slim GraphQL query with JSON fallback. Do not reintroduce GraphQL schema generation for items/tasks/hideout.

## json.tarkov.dev (bulk catalog)

Authoritative entry: `src/App/TarkovDevAPI.cs`.

- Base: `https://json.tarkov.dev`
- Paths are game-mode + document, e.g. `{regular|pve}/items`, locale overlays `{mode}/items_{locale}`, same pattern for tasks/hideout/maps.
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

## Maps: slim GraphQL + fallback

Intentional dual path (documented on `TarkovDevAPI`):

1. Prefer slim GraphQL query selecting only `id`, `name`, `normalizedName` on `https://api.tarkov.dev/graphql`.
2. On failure or empty: extract maps dictionary from json.tarkov.dev without loading unrelated multi-MB siblings (`ExtractMapsDictionary` — unit-tested).
3. Maps stay **off cold-start critical path** (background queue + offline projected cache).

`MapDataLoader` combines local interactive `maps.json` with the live catalog ids; empty catalog means “not ready yet” (retryable), not permanent failure.

## Crafts / barters / acquisition hints

- `/crafts` and `/barters` JSON endpoints are fetched and indexed via helpers on `TarkovDevAPI`.
- Presentation layer can surface craft/barter/FIR need hints (`Presentation/ResultPresentationServices` and related).
- Keep heavy recommendation work off blocking cold start unless product requirements change and docs update.

## TarkovTracker

- Config: `RatConfig.Tracking.TarkovTracker` (token DPAPI field, backend enum; default **ORG** / `api.tarkovtracker.org`, IO / `tarkovtracker.io` is legacy).
- `TarkovTrackerDB` holds progress; refresh timer after init.
- `APIClient` performs bearer GETs with shared UA and maps unauthorized/rate-limit exceptions.
- Models: `FetchModels/TarkovTracker/*`.

Invalid token paths clear config and warn the user; do not log tokens.

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
