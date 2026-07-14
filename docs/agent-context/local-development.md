# Local development

## Prerequisites

- **64-bit Windows** desktop (required; OpenCvSharp native runtime is x64-only).
- [.NET 10 SDK](https://dotnet.microsoft.com/download) matching CI (`10.0.x`).
- Network for first-time NuGet restore and RatScannerData download.
- WebView2: installed automatically at runtime if missing; manual install still fine.
- Optional: 7-Zip on PATH for publish zipping (`publish.bat` falls back to `Compress-Archive`).
- Optional for markdown lint/auto-fix: [Node.js LTS](https://nodejs.org/) (npm). Used only by `scripts\lint-markdown.ps1` / CI — not required to run the app.

## Windows-only restriction

Do not run or document the app under x86 Windows, WSL, or Linux. Targeting and native dependencies assume 64-bit Windows.

## SDK / runtime expectations

- App TFM: see `src/App/RatScanner.csproj` (`net10.0-windows10.0.22621.0`).
- ScanEngine: `netstandard2.0` (consumed by App).
- Tests: same Windows TFM family as App.
- App and tests target x64 in every configuration; x86 solution configurations are intentionally absent.
- App disables implicit usings — code must keep explicit `using` directives.

## Initial setup

From repo root:

```bat
dev.bat
```

First run (or incomplete Data) executes `scripts\setup-data.ps1`, restores the solution, then starts the watch loop.

Manual data only:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup-data.ps1
powershell -ExecutionPolicy Bypass -File scripts\setup-data.ps1 -Force
```

Data source: GitHub release `RatScanner/RatScannerData` → `Data.zip` into **`src\App\Data\`**.

### Data readiness checks

Scripts treat Data as ready when all exist:

- `maps.json`
- `unknown.png`
- `traineddata\eng.traineddata`
- `icons\` with at least one `.png`

`RatScanner.csproj` copies `Data\**` to output with `Watch=false` so icon dumps do not flood `dotnet watch`.

## Day-to-day commands

| Intent | Command |
| --- | --- |
| Watch (default) | `dev.bat` or `scripts\dev.ps1` |
| One-shot run | `dev.bat -Once` |
| Force Data reinstall | `dev.bat -ForceSetup` |
| Release config | `dev.bat -Release` |
| Skip restore | `dev.bat -SkipRestore` |
| IDE F5 | Open `RatScanner.sln`, run App project |
| Markdown check / auto-fix | `scripts\lint-markdown.ps1` / `-Fix` |
| Optional markdown pre-commit hook | `scripts\install-git-hooks.ps1` |

What watch does (`scripts/dev.ps1`):

1. Ensure Data.
2. `dotnet restore RatScanner.sln` (unless skipped).
3. `dotnet watch --project src\App\RatScanner.csproj --non-interactive --no-hot-reload run …`

**Restart-on-save** is intentional. Full in-process hot reload is unreliable for this WPF/WebView stack.

Equivalent one-shot without the bat:

```powershell
dotnet run --project src\App\RatScanner.csproj
```

## Debug workflow

- `dev.bat` Debug config by default; attach Visual Studio/Rider to the launched process if needed.
- With debugger attached, main/overlay WebViews open DevTools.
- Logs: `Log.txt` next to the running exe; RatEye may write `RatEyeLog.txt`.
- Config: `config.cfg` next to the running exe (bin output during debug).

## Generated / downloaded locations

| Path | Commit? | Notes |
| --- | --- | --- |
| `src/App/Data/**` | No (gitignored) | Icons, OCR, maps.json |
| `src/App/bin/**`, `obj/**` | No | Build + copied Data/i18n/wwwroot |
| `%TEMP%\RatScanner\Cache\` | No | Offline API projections (`RatConfig.Paths.CacheDir`) |
| `publish/` | No | Local publish output |
| `data/bench/` | No | Local measurements |

## Common setup failures

| Symptom | Likely cause | Mitigation |
| --- | --- | --- |
| setup-data download fails | Network / TLS / proxy | Retry; scripts try curl fallback |
| Zip extract fails | Broken `Expand-Archive` | `Expand-Zip.ps1` falls through to .NET ZipFile / python |
| App exits code 2 | Second instance | Close existing RatScanner |
| App exits code 3 / WebView error | WebView2 missing and install failed | Install Evergreen WebView2 Runtime manually |
| Scan never matches | Data missing or wrong scale/display | Re-run ForceSetup; check game display settings |
| Empty maps overlay | maps.json missing or API maps not ready | Data install + wait for background maps load |
| Restore/build fails on non-Windows or x86 | Wrong platform | Use 64-bit Windows |

## What not to do while iterating

- Do not use `publish.bat` as the everyday loop (slow; packages single-file).
- Do not commit `src/App/Data` or `publish/`.
- Do not assume NuGet RatEye is a valid fix for scan bugs — edit `src/ScanEngine/`.
