# Localization

## Two different “languages”

| Concern | Mechanism | Config |
| --- | --- | --- |
| **UI chrome strings** | JSON dictionaries + `LocalizationService` | `RatConfig.UserInterface.Language` (`UiLanguage`) |
| **In-game name OCR / item locale API** | RatStash `Language` + json.tarkov.dev locale files | `RatConfig.NameScan.Language` |

Do not conflate UI locale codes with OCR/API locale when editing settings or docs.

## UI localization system

- Service: `src/App/LocalizationService.cs`
- Files: `src/App/i18n/{en,es,fr,pl,pt,ru,zh}.json`
- Load path: `RatConfig.Paths.I18nDir` + `UiLanguage.GetTranslationFileName()`
- API: `Translate(key)`, indexer, `Format(key, args)` with `CultureInfo.CurrentCulture`
- Missing/malformed selected catalog: log warning and fall back to English.
- Missing selected-language key: use the English value, then the key itself if English also lacks it.

English (`en.json`) is the **baseline catalog of keys**.

The build includes `i18n/` beside the executable. If content or packaging rules change, re-verify every locale is present in build and publish output.

## Adding or changing strings

1. Add/change the key in `en.json`.
2. Update **every** other locale file with the best available translation (or English temporary text if no translation — do not omit the key).
3. Use `Localizer["Key"]` / `Localizer.Format(...)` in Razor/C# — avoid hardcoding new user-visible English in UI paths that already use localizer.
4. Prefer stable key names (`PascalCase` identifiers matching existing style).

## Validation

- `LocalizationServiceTests` enforces an exact English key set across packaged locale catalogs and covers fallback/error behavior.
- Manual: switch UI language in General settings and spot-check screens.
- When packaging changes touch content includes, confirm `i18n` appears beside the published exe.

## What is not localized here

- Item names from tarkov.dev (API locale / English fallbacks).
- Many error dialogs may still be English-first depending on call site — when editing, prefer consistency with surrounding code.
