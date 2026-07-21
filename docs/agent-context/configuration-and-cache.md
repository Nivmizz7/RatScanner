# Configuration and cache

## Runtime config file

- Path: `config.cfg` next to the running executable (`RatConfig.Paths.ConfigFile`).
- Format: Windows INI-style sections via `SimpleConfig` (kernel32 private profile APIs).
- Load/save: `RatConfig.LoadConfig()` / `SaveConfig()`; the current `ConfigVersion` value lives in code.
- Unsupported/missing version: preserve the original as `config.cfg.v<version>.bak` (with a numeric suffix if needed), read every compatible value, apply safe defaults for unreadable/new fields, then save the current format.
- If the backup cannot be created, load readable values for the session but do not automatically rewrite the original file.
- Sensitive values (e.g. tracker token): `WriteSecureString` / DPAPI `ProtectedData` (CurrentUser).
- Save uses a temp file then move for safer replacement.

Do not check config files with secrets into the repository.

## Configuration surface (`RatConfig`)

Logical groups (nested static classes):

| Group | Examples |
| --- | --- |
| `NameScan` | enable, auto, language, confidence, geometry |
| `IconScan` | enable, rotated icons, hotkey, cache icons |
| `ToolTip` | duration, digit grouping |
| `UserInterface` | UI language |
| `MinimalUi` | field visibility, opacity |
| `Tracking` / `TarkovTracker` | DPAPI-protected PvP/PvE `.org` keys, legacy PvP-only `.io` key, team, refresh |
| `Overlay.Search` | enable, blur, hotkey |
| Other top-level | game mode, always on top, tray, TTLs, window position/mode |

Game display preferences (monitor id, custom resolution/scale) are also on `RatConfig` and refreshed through `Display/*` services (`WindowsGameDisplayService`, `GameDisplayPreferencesStore`).

## Defaults and migration

- Defaults live as field initializers on `RatConfig` nested types.
- `ConfigVersion` decides whether migration and backup are required; individual settings still use tolerant typed reads with defaults.
- Unsupported or unversioned config → preserve original bytes, best-effort migrate readable fields, and rewrite only after the backup succeeds.
- Backups are never overwritten; repeated migrations choose the next available suffix.
- Prefer explicit version bumps when field semantics change, and add migration regression coverage.

## Path constants

Defined under `RatConfig.Paths` (base = exe directory):

| Path | Use |
| --- | --- |
| `Data` | Icons, traineddata, maps.json, locales on disk |
| `StaticIcon` | `Data/icons` |
| `TrainedData` | OCR data |
| `CacheDir` | `%TEMP%\RatScanner\Cache` API offline files |
| `I18nDir` | `i18n` under base (UI strings) |
| `ConfigFile` | `config.cfg` |
| `LogFile` | `Log.txt` |
| Dynamic EFT icon cache | Battlestate Games temp `Icon Cache` |

When code assumes paths, prefer these constants over string literals.

## API offline cache

Helpers:

- `RatConfig.GetCachePath(key)` — SHA-256 hex of key + `.data`
- `WriteToCache` — atomic temp write + move
- `ReadFromCache` — content + `LastWriteTimeUtc` for age

TTL policy lives in `TarkovDevAPI` using `RatConfig` TTL fields and file mtime — not a separate redis/service.

## Display / resolution handling

- Active game display configuration is refreshed on a short interval under a lock.
- Settings UI can force monitor, custom resolution, or scale overrides.
- Capture geometry and RatEye scale depend on the resolved configuration — wrong monitor is a top cause of failed scans.

## UI settings flow

- Complete choices (switches, selects, presets) apply immediately through `SettingsVM` / `SettingsPersistenceService`, then write the complete config atomically. A failure restores only the affected setting's last persisted value.
- Editable capture fields keep local draft text and save on blur or Enter only after display-aware validation. Escape reloads the persisted display preferences.
- TarkovTracker API keys are never saved while typing. The user explicitly tests a key; only successful mode and permission validation commits it.
- There is no global Settings Save/Cancel bar.

Game mode is exposed in the app sidebar as an immediate PVP/PVE selector. A switch refreshes the selected mode's catalog caches, rebuilds RatEye item data, updates current scan items, selects the matching tracker credential/progress cache, and persists `RatConfig.GameMode`; failure restores the previous mode.

## Advanced overrides

Hotkeys, tray minimize, always-on-top, game mode (regular/PVE), and mode-specific tracker credentials are first-class settings. Prefer extending existing sections over inventing side channel files.

## Agent rules

- Preserve backup-before-migration behavior; bump `ConfigVersion` only with intentional migrations and tests.
- Do not weaken DPAPI protection or log secrets.
- Cache correctness: prefer extending existing Read/Write helpers over inventing parallel stores.
- After changing path layout, update setup-data readiness checks, csproj copy rules, and this doc.
