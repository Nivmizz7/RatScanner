# Repository map

Organized by **concern**. Exact filenames change; start here, then confirm on disk.

## Solution layout

```text
RatScanner.sln
src/App/                 # WPF + Blazor app (assembly RatScanner)
src/ScanEngine/          # Standalone RatEye Git submodule
tests/RatScanner.Tests/  # xUnit tests
scripts/                 # dev, data setup, zip helper, agent-docs check, markdown lint, optional bench
.github/workflows/       # CI build + release
docs/agent-context/      # This documentation set
media/                   # README demos
examples/                # Sample resolution screenshots
```

## Startup and host

| Concern | Where |
| --- | --- |
| Entry | `src/App/Program.cs` |
| WPF Application | `App.xaml`, `App.xaml.cs` (single-instance, WebView2 install) |
| Main window / tray | `PageSwitcher.xaml(.cs)` |
| Domain bootstrap | `RatScannerMain.cs` |
| Branding constants | `Constants.cs` |

## UI and navigation

| Concern | Where |
| --- | --- |
| Blazor host controls | `View/BlazorUI`, `BlazorOverlay` |
| Minimal WPF UI | `View/MinimalMenu` |
| Root router | `RazorApp.razor` → `/app` |
| App shell layout | `Shared/AppLayout`, `MainLayout`, `SettingsLayout`, `OverlayLayout` |
| Pages | `Pages/App/*`, `Pages/Overlay/*` |
| Shared components | `Shared/ScannerStatus`, `Components/HotkeySelector` |
| Host HTML | `wwwroot/index.html`, `overlay.html` |
| Global theme CSS | `wwwroot/css/theme.css` |
| Scoped page CSS | co-located `*.razor.css` |

## Presentation models

| Concern | Where |
| --- | --- |
| Menu / settings VMs | `ViewModel/MenuVM.cs`, `SettingsVM.cs` |
| Scan result adapters | `Presentation/*` |
| Display detection models | `Display/*` |

## Scan pipeline (App side)

| Concern | Where |
| --- | --- |
| Capture + orchestrate | `RatScannerMain` scan methods |
| Scan DTOs / queue | `Scan/*` |
| Hotkeys | `Hotkey.cs`, `HotkeyManager.cs`, `ActiveHotkey.cs`, `UserActivityHelper.cs` |

## Scan engine (processing)

| Concern | Where |
| --- | --- |
| Engine facade | `src/ScanEngine/RatEye/RatEyeEngine.cs` |
| Config | `src/ScanEngine/RatEye/Config/*` |
| Inspection / inventory / icon | `src/ScanEngine/RatEye/Processing/*` |
| Icon templates | `src/ScanEngine/RatEye/IconManager.cs`, `Resources/` |
| Replay benchmark | `src/ScanEngine/RatEye.Benchmarks` |

## Data integrations

| Concern | Where |
| --- | --- |
| tarkov.dev client | `TarkovDevAPI.cs` |
| Domain models | `TarkovDev/Models.cs`, `JsonApiModels.cs`, `Acquisition.cs` |
| Interactive map data | `MapDataLoader.cs`, `InteractiveMapData.cs`, `Data/maps.json` (downloaded) |
| TarkovTracker | `TarkovTrackerDB.cs`, `APIClient.cs`, `FetchModels/TarkovTracker/*` |
| Updates | `GitHubUpdateService.cs` |

## Configuration

| Concern | Where |
| --- | --- |
| Settings + paths + cache helpers | `RatConfig.cs` |
| INI read/write + secure strings | `SimpleConfig.cs` |
| Runtime config file | `config.cfg` next to executable (not in repo) |

## Localization

| Concern | Where |
| --- | --- |
| Service | `LocalizationService.cs` |
| UI strings | `src/App/i18n/*.json` (en, es, fr, pl, pt, ru, zh) |
| OCR language | RatStash `Language` via name-scan settings (separate from UI language) |

## Static assets and runtime data

| Concern | Where |
| --- | --- |
| Icons / OCR / maps payload | `src/App/Data/**` (gitignored; from a pinned `tarkovtracker-org/RatScannerData` release with checksum + manifest validation) |
| App resources | `src/App/Resources/*` |
| Web static | `wwwroot/**` |

## Tests

| Concern | Where |
| --- | --- |
| Project | `tests/RatScanner.Tests/` |
| Scoped instructions | `tests/AGENTS.md` |
| Coverage examples | display/config migration, localization, API/cache/presentation, tracker/update reliability, synthetic OpenCV pipeline |

## Development scripts

| Script | Role |
| --- | --- |
| `dev.bat` / `scripts/dev.ps1` | Local watch or one-shot run |
| `scripts/setup-data.ps1` | Install the pinned, checksum/manifest-verified Data release into the app or a caller-provided destination |
| `scripts/RatScannerData.ps1` | RatScannerData release pin and reusable checksum/manifest/payload validators |
| `scripts/test-data-validation.ps1` | Hermetic regression tests for the RatScannerData validators (also CI) |
| `scripts/verify-package.ps1` | Verify a packaged `RatScanner.zip` against the pinned data contract (also CI, after zipping) |
| `scripts/Expand-Zip.ps1` | Robust zip extract fallbacks |
| `scripts/check-agent-docs.ps1` | Structural documentation integrity (also CI) |
| `scripts/test-agent-docs.ps1` | Disposable adversarial regression fixture for the integrity check (also CI) |
| `scripts/lint-markdown.ps1` | markdownlint-cli2 check / `-Fix` auto-fix (also CI check) |
| `scripts/install-git-hooks.ps1` | Optional local pre-commit Markdown check (no mutation/re-staging) |
| `package.json` / `.markdownlint-cli2.jsonc` | Dev-only Node markdownlint tooling (not app runtime) |
| `publish.bat` | Local release package |
| `scripts/bench/*` | Optional perf measurements (not product path) |

## Publishing and CI

| Concern | Where |
| --- | --- |
| Local publish | `publish.bat` → `publish/`, `RatScanner.zip` |
| Build CI | `.github/workflows/build.yml` |
| Release promotion | `.github/workflows/release.yml` |
| Version | `<Version>` in `src/App/RatScanner.csproj` only (product) |

## Formatting / analyzers

| Concern | Where |
| --- | --- |
| CSharpier | `dotnet-tools.json`, `.csharpierrc.json`, `.csharpierignore`; invoke via `dotnet tool restore` then `dotnet csharpier check .` / `format .` |
| Editor defaults | `.editorconfig`, `.vscode/settings.json` |

## Source dependency

`src/ScanEngine` is the standalone RatEye submodule. Initialize it recursively and do not reintroduce a NuGet RatEye dependency.

## Intentionally not product source

- `data/bench/` — local benchmarks / publish smoke (gitignored patterns apply)
- `publish/` — publish output
- `bin/` / `obj/` — build intermediates
