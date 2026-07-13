# RatScanner Agent Guide

## Project Overview

RatScanner is a Windows-only .NET WPF application for Escape from Tarkov that scans items in-game via screenshots and image processing, then displays pricing and item data from the [tarkov.dev](https://tarkov.dev) API.

- **Framework:** .NET 10.0 WPF (`net10.0-windows10.0.22621.0`)
- **UI:** MudBlazor via WebView, WPF host
- **Key deps:** in-repo RatEye (image processing), RatStash (item database NuGet), Tesseract (OCR), Newtonsoft.Json
- **Platform:** Windows only — cannot be built or run in WSL/Linux

## Local Development Loop

**Day-to-day:** use `dev.bat` (or `scripts\dev.ps1`). Do **not** use `publish.bat` while iterating.

```sh
dev.bat                 # watch: auto rebuild + restart on save
dev.bat -Once           # build + run once
dev.bat -ForceSetup     # re-download RatScannerData icons/OCR
```

What `dev.bat` does:

1. Ensures `RatScanner\Data\` exists (`scripts\setup-data.ps1`, skipped if already valid)
2. Restores NuGet packages
3. Runs `dotnet watch run` (restart-on-save)

WPF does not get reliable full hot reload for C#/XAML. Best practice is **restart-on-save** via `dotnet watch`, which is what the script uses. Manual close/rebuild/`publish\` is unnecessary for normal work.

One-time / manual data install:

```sh
powershell -ExecutionPolicy Bypass -File scripts\setup-data.ps1
```

The csproj copies `RatScanner\Data\**` to the output directory (`CopyToOutputDirectory=PreserveNewest`), so debug runs under `RatScanner\bin\...` pick up icons/OCR automatically.

Shared zip helper: `scripts\Expand-Zip.ps1` (Expand-Archive → .NET ZipFile → python). Used by setup-data and `publish.bat` when PowerShell Archive is broken.

## Build Commands

```sh
dotnet restore RatScanner.sln        # restore NuGet packages
dotnet build RatScanner.sln          # build (debug)
dotnet build -c Release RatScanner.sln  # build (release)
dotnet test RatScanner.sln           # unit tests (RatScanner.Tests)
dev.bat                              # local watch loop (preferred while coding)
publish.bat                          # release single-file package only
```

Verify changes by building (and running tests when behavior is covered). GraphQL models come from the `GraphQlClientGenerator` source generator at build time — do not check in generated client sources under `obj/`.

### GraphQL ambiguity / "already contains a definition" flood

If the IDE or build reports thousands of errors like `Ambiguity between 'Item.Id' and 'Item.Id'` or `namespace 'RatScanner.TarkovDev.GraphQL' already contains a definition`, stale emitted sources under `RatScanner/obj/generated/` are almost always the cause (duplicate of the live generator output). Fix:

```sh
dotnet clean RatScanner.sln
# if obj\generated still exists:
# Remove-Item -Recurse -Force RatScanner\obj\generated
dotnet build RatScanner.sln
```

Do not try to "fix" hand-written call sites for these ambiguity errors.

## Repository Layout

- `RatScanner/` — main application project (WPF / MudBlazor)
  - `RatScannerMain.cs` — entry point, startup flow, update check
  - `TarkovDevAPI.cs` — GraphQL API client with rate limiting, caching, dedup
  - `RatConfig.cs` — configuration, cache path logic
  - `MapDataLoader.cs` — map data loading
  - `RatScanner.csproj` — project file, target framework, package + project references
- `RatEye/` — vendored image-processing library (ProjectReference, not NuGet)
  - Sources from upstream `RatScanner/RatEye` tag `v4.0.1` (see `RatEye/VENDOR.md`)
  - Change scan/marker/OCR behavior here; ship with the app in one PR
- `RatScanner.sln` — solution (RatScanner + RatEye)
- `publish/` — publish assets (gitignored; produced by `publish.bat`)
- `publish.bat` — release single-file package (not for day-to-day)
- `dev.bat` / `scripts\dev.ps1` — local watch loop (preferred while coding)
- `scripts\setup-data.ps1` — download icons/OCR into `RatScanner\Data\`
- `scripts\Expand-Zip.ps1` — robust zip extract helper
- `media/` — README images
- `examples/` — example files
- `.github/` — GitHub Actions workflows

## Fork And Branch Workflow

This repo is a maintained fork. Upstream (`RatScanner/RatScanner`) is inactive.

- **`origin`** → `tarkovtracker-org/RatScanner` (this fork — push here)
- **`upstream`** → `RatScanner/RatScanner` (original — sync only, rarely)
- **Primary branch:** `master` (not `main`)
- Work directly on `master` for integration. Use short-lived feature branches (`fix/...`, `feat/...`) for non-trivial changes, merged via PRs on the fork.
- When referencing upstream issues/PRs, use full URLs. Bare `#NNN` resolves to the fork which has no issues.
- `git push` and `gh` commands default to `origin` (fork). Always verify the target repo when using GUI tools.

## Orca Configuration

This repo is managed in Orca with the following settings:

- **Base ref:** `origin/master` (new worktrees branch from the fork, not upstream)
- **Setup script:** `dotnet restore RatScanner.sln` (runs on every new worktree)
- **Setup policy:** `run-by-default`
- **Fork sync mode:** `safe-auto`
- **Git remote identity:** `origin` (fork) — PR/issue UI defaults to the fork
- **Git username:** `DysektAI`

New worktrees are created under `C:/Users/Dysekt/orca/workspaces/RatScanner/` and branch from `origin/master` with auto-prefixed branch names (`DysektAI/<name>`).

## Conventions

- Follow the existing code style — check surrounding code before editing.
- The project uses git flow historically (`develop` branch, feature branches from develop) but the maintained fork works directly on `master`.
- Versioning follows semver (`Major.Minor.Patch`) — bump in `RatScanner.csproj`.
- Nullable reference types are enabled — handle nulls explicitly.
- Cache freshness is based on file modification time (see `RatConfig.cs`).
- API calls go through `TarkovDevAPI.cs` which handles rate limiting, dedup, and exponential backoff — do not bypass it with raw HTTP calls.
- Do **not** re-add a NuGet `PackageReference` for `RatEye`. Edit `RatEye/` in-tree and reference via `ProjectReference`.
