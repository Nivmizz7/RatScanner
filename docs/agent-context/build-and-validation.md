# Build and validation

## Commands

### Restore

```bat
dotnet restore RatScanner.sln
```

(`dev.bat` restores unless `-SkipRestore`.)

CI caches the global NuGet package directory using the project files and `NuGet.Config` as the key input, but still runs an explicit solution restore on every job.

### Build

```bat
dotnet build RatScanner.sln
dotnet build -c Release RatScanner.sln
```

App and tests target x64 in every configuration because the OpenCvSharp Windows native runtime is x64-only. Release omits debug symbols (see App csproj).

### Unit tests

```bat
dotnet test RatScanner.sln
dotnet test RatScanner.sln -c Release --no-restore
```

CI builds and tests Release on `windows-latest` with .NET 10.x.

Current App test project: `tests/RatScanner.Tests` (xUnit v3). It covers App logic and reliability contracts, configuration migration, localization fallback/key parity, and the optional App-owned capture/crop harness. RatEye's submodule owns engine/OpenCV/cache tests and fixture replay. **Neither** is a substitute for full UI or live-scan verification. Scoped rules: `tests/AGENTS.md`.

### Formatting

Authoritative tool for C#: **CSharpier** as a local tool (see `dotnet-tools.json`).

```bat
dotnet tool restore
dotnet csharpier check .
dotnet csharpier format .
```

Do not invent alternate invocations solely from config files. Config: `.csharpierrc.json` (spaces, width 120, CRLF). Ignore: `.csharpierignore` (XAML, csproj, resx, bin/obj, …).

VS Code defaults to CSharpier on save for C# (`.vscode/settings.json`).

### Markdown lint

Authoritative tool: **markdownlint-cli2** via Node (dev-only `package.json`; not a product runtime dependency).

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\lint-markdown.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\lint-markdown.ps1 -Fix
npm run lint:md
npm run lint:md:fix
```

Config: `.markdownlint-cli2.jsonc` (CLI globs/ignores) + `.markdownlint.json` (rule set for CLI and VS Code extension). Rules of note:

- **Tables:** compact GFM style (MD055/MD060). Delimiter rows must be `| --- | --- |` (spaces around pipes), not `|---|---|` or `|--------|`. The pinned CLI in `package.json` auto-fixes this style; VS Code fix-on-save also applies once the file is open.
- **Fenced code:** language tag required (MD040) — not always auto-fixed; use `text`, `bat`, `powershell`, etc.
- **Line length (MD013):** disabled — long agent/prose lines and table cells are fine
- Script installs npm deps on first run when `node_modules` is missing

Optional local pre-commit check: `scripts\install-git-hooks.ps1`. It runs only when Markdown is staged and never rewrites or re-stages working-tree content. CI runs check mode after Node setup.

VS Code: recommend `davidanson.vscode-markdownlint`; format/fix-on-save for Markdown is enabled in `.vscode/settings.json`.

### Documentation integrity

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-agent-docs.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-agent-docs.ps1
```

The integrity check validates required paths, context routing, local Markdown links, structurally parsed MSBuild references/package versions, RatEye submodule wiring, and primary-branch consistency while excluding generated output. The adversarial test builds a disposable path-with-spaces fixture and proves representative failures are non-zero and actionable. Both run in CI.

### RatScannerData validator tests

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-data-validation.ps1
```

These hermetic fixtures cover checksum parsing/mismatch handling, manifest schema and count validation, required files, root/nested archive layouts, and packaged-archive verification. They do not download live data. The later packaging step downloads the pinned release and validates the real published assets.

### Release package verification

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-package.ps1
```

Installation validation only proves the staged `publish\Data` tree was correct; packaging happens afterwards. This runs against the archive that actually ships — reading the manifest and every manifest-listed payload byte out of the zip — so a packaging step cannot silently drop, truncate, or duplicate payload files. It also rejects duplicate entry names and entries that differ only by case, which collide on extraction on the Windows x64 target. `publish.bat` and CI both run it on the zip they just produced, before that artifact can be uploaded or promoted. Entry separators are normalized because 7-Zip writes forward slashes and `Compress-Archive` writes backslashes.

### Analyzers / warnings

- Nullable enabled on App; treat nullability seriously.
- Implicit usings disabled on App — do not rely on global usings.
- Release does not treat all warnings as errors (`TreatWarningsAsErrors=false` currently). Still fix warnings you introduce.

### Package vulnerability / outdated checks

CI gates on the transitive .NET vulnerability audit and high/critical npm audit for the pinned Markdown tooling. The outdated report remains an intentional review command because this repository retains some versions for compatibility:

```bat
dotnet list RatScanner.sln package --vulnerable --include-transitive
dotnet list RatScanner.sln package --outdated
```

Interpret carefully (transitive noise). Do not upgrade casually — see `dependency-management.md`.

## Manual verification tiers

### Unit tests (automated)

Fast, headless. Prefer expanding these for pure logic (parsers, projections, pure presentation helpers).

### WebView / UI smoke (manual)

With `dev.bat` or `-Once`:

- Main window loads Blazor shell (not stuck on “Loading…”).
- Navigation: Scan, History, Settings, Credits.
- Item search autocomplete returns results when catalog cache is warm.
- Scanner status chip shows ready/degraded appropriately.
- Theme/CSS changes: visual check for layout regressions (search bar, sidebar).
- DPI / window resize: compact header and sidebar remain usable.

### Scan regression (manual / environment)

Requires game or carefully prepared screens + correct Data:

- Name scan on inspect window.
- Icon scan with configured modifier.
- Wrong resolution / display selection → adjust settings and retest.
- Use `examples/` images only as rough references; live matching depends on Data + scale.

### Localization validation

- Switch UI language in settings; spot-check key screens.
- Missing keys fall back to key string — not acceptable for new user-facing text.
- Keep all locale files in `src/App/i18n/` aligned with `en.json` keys.

### Publish validation

```bat
publish.bat
```

Confirm:

- `publish\RatScanner.exe` starts.
- `publish\LICENSE` present.
- `publish\Data\manifest.json` is schema 1 and declares the extracted icon count.
- `publish\Data\` contains maps.json / icons / traineddata.
- The setup log confirms the pinned archive checksum and published/embedded manifest validation.
- `RatScanner.zip` created, verified by `scripts\verify-package.ps1`, and containing no temporary `Data.zip` or checksum download files.
- Single-file self-contained win-x64 matches CI intent.

CI uploads the validated `RatScanner.zip` as an immutable build artifact. The separate least-privilege Release workflow promotes that exact artifact only after verifying a successful push-triggered Build for the selected `master` commit; it does not rebuild (see `release-and-versioning.md`).

## Required checks by change category

| Change | Required | Recommended |
| --- | --- | --- |
| Pure logic / helpers | build + tests for covered code | csharpier |
| API client / cache | build + unit tests | manual warm/cold start smoke |
| UI Razor/CSS | build | WebView smoke |
| RatEye processing | standalone RatEye build/tests + integrated App build | fixture replay + scan smoke if accuracy-sensitive |
| Config paths / display | build + tests | manual multi-monitor if possible |
| i18n | build | all locale files updated; UI language switch |
| CI / scripts | relevant hermetic script test + build (or script dry-run) | publish path once if packaging changed |
| RatScannerData pin / packaging | `test-data-validation.ps1` + setup against the pinned release + `verify-package.ps1` on the built zip + Release build/test | current-EFT fixture replay and clean-machine smoke |
| Agent docs / layout | `check-agent-docs.ps1` + markdown lint | `test-agent-docs.ps1` when changing the checker |
| Version / release notes | csproj version + docs | tag workflow locally understood |
| Docs only / any `*.md` | `lint-markdown.ps1 -Fix` then check; `check-agent-docs.ps1` when structure/links change | |

## Distinguishing test layers

| Layer | What it proves | What it does not |
| --- | --- | --- |
| Unit tests | Pure functions, contracts, regressions that were encoded | Real OCR accuracy, WebView rendering, end-user scan UX |
| Build | Compiles against current TFMs/packages | Runtime asset presence beyond compile |
| Manual UI | WebView wiring, CSS, navigation | Automated coverage |
| RatEye fixture replay | Recorded accuracy/latency for versioned captures | Every display/game configuration |
| Manual scan | End-to-end recognition path | Continuous regression by itself |
| Doc integrity script | Structural doc + packaging constraints | Narrative accuracy of every sentence |

Do not claim “fully tested” when only `dotnet build` succeeded.
