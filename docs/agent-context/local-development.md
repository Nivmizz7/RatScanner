# Local development

## Prerequisites

- **64-bit Windows** desktop (required; OpenCvSharp native runtime is x64-only).
- [.NET 10 SDK](https://dotnet.microsoft.com/download) selected by the root `global.json`; CI installs from the same file.
- Network for first-time NuGet restore and RatScannerData download.
- WebView2: installed automatically at runtime if missing; manual install still fine.
- Initialized RatEye submodule (`git submodule update --init --recursive`).
- Optional: 7-Zip on PATH for publish zipping (`publish.bat` falls back to `Compress-Archive`).
- Optional for markdown lint/auto-fix: [Node.js LTS](https://nodejs.org/) (npm). Used only by `scripts\lint-markdown.ps1` / CI — not required to run the app.

## Windows-only restriction

Do not run or document the app under x86 Windows, WSL, or Linux. Targeting and native dependencies assume 64-bit Windows.

## SDK / runtime expectations

- Root `global.json` pins a patch floor within the SDK feature band CI builds with (`latestPatch` roll-forward), so compiler and bundled-analyzer behavior stays reproducible and machine-side patch servicing cannot break the repo.
- App TFM: see `src/App/RatScanner.csproj` (`net10.0-windows10.0.22621.0`).
- RatEye library: `netstandard2.0` (consumed from the submodule by App).
- Tests: same Windows TFM family as App.
- App and tests target x64 in every configuration; x86 solution configurations are intentionally absent.
- App disables implicit usings — code must keep explicit `using` directives.

## Initial setup

From repo root:

```bat
dev.bat
```

Every run delegates the Data decision to `scripts\setup-data.ps1`, which validates the existing installation against the pinned contract and exits early when nothing needs to change. It then restores the solution and starts the watch loop. `dev.ps1` deliberately keeps no readiness predicate of its own, because a second predicate drifts from the contract.

Manual data only:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup-data.ps1
powershell -ExecutionPolicy Bypass -File scripts\setup-data.ps1 -Force
```

Data source: pinned GitHub release `tarkovtracker-org/RatScannerData@data-f1f047dc5d38ee43` into **`src\App\Data\`**. `setup-data.ps1` downloads `Data.zip`, `Data.zip.sha256`, and `manifest.json`, verifies the archive against both the published checksum and the repository pin, and validates the embedded manifest before replacing an existing installation.

Use `-DestinationPath <path>` when the verified payload belongs somewhere other than `src\App\Data`, such as `publish\Data`. Advancing the data release requires updating the tag and archive SHA-256 together in `scripts\RatScannerData.ps1` after validating the new release.

### Data readiness checks

Scripts treat Data as ready only when the embedded schema-1 `manifest.json` is valid, its icon count matches the extracted files and exceeds the sanity floor, and all of these exist:

- `manifest.json`
- `maps.json`
- `unknown.png`
- `traineddata\eng.traineddata`
- the exact number of `icons\*.png` declared by the manifest

Readiness additionally requires the installed `contentSha256` to match the pinned release tag, and `setup-data.ps1` re-hashes every manifest-listed file before skipping a reinstall. Without those two checks a payload from a previously pinned release, or one with a corrupted file, would satisfy the skip path.

`RatScanner.csproj` copies `Data\**` to output with `Watch=false` so icon dumps do not flood `dotnet watch`.

## Day-to-day commands

| Intent | Command |
| --- | --- |
| Watch (debounced, default) | `dev.bat` or `scripts\dev.ps1` |
| Watch (instant restart) | `dev.bat -NoDebounce` |
| Custom quiet period | `dev.bat -Debounce 8` |
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
3. Default: debounced FileSystemWatcher on `src\` — after a quiet period (15s with no file edits) it kills the running app, rebuilds, and relaunches. Changes during a build are coalesced: after launch, if new edits occurred, the quiet timer restarts and triggers one more rebuild.
4. `-NoDebounce`: falls back to `dotnet watch --project src\App\RatScanner.csproj --non-interactive --no-hot-reload run …` (rebuild within ~1-2s of each save).

**Restart-on-save** is intentional. Full in-process hot reload is unreliable for this WPF/WebView stack. The debounce prevents endless close/reopen cycles when an agent edits files in rapid bursts.

Equivalent one-shot without the bat:

```powershell
dotnet run --project src\App\RatScanner.csproj
```

## Debug workflow

- `dev.bat` Debug config by default; attach Visual Studio/Rider to the launched process if needed.
- With debugger attached, main/overlay WebViews open DevTools.
- Logs: `Log.txt` next to the running exe; RatEye may write `RatEyeLog.txt`. `Log.txt` includes the startup timeline and a `perf` line per scan — see [performance-diagnostics.md](performance-diagnostics.md).
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
- Do not assume NuGet RatEye is a valid fix for scan bugs. Make engine changes in the RatEye submodule, commit RatEye first, then update RatScanner's gitlink.
