# Rat Scanner

![RatScanner logo](media/RatLogo.png)

Actively maintained by [TarkovTracker.org][tarkovtracker].

This is a **modified** community build of RatScanner (see [Attribution & license](#attribution--license) at the bottom).

**Versioning:** this build uses an independent semver line starting at **`v4.0.0`**. Historical / original RatScanner used `3.x` — if a report says `3.9.x`, it is **not** this build.

[![GitHub](https://img.shields.io/badge/GitHub-tarkovtracker--org%2FRatScanner-181717?style=for-the-badge&logo=github)](https://github.com/tarkovtracker-org/RatScanner)
[![Discord](https://img.shields.io/badge/Discord-TarkovTracker-7389D8?style=for-the-badge&logo=discord&logoColor=ffffff&labelColor=6A7EC2)](https://discord.gg/M8nBgA2sT6)
[![Download](https://img.shields.io/static/v1?&label=&message=Download&color=4FBD54&style=for-the-badge&logo=data:image/svg+xml;base64,PHN2ZyB2ZXJzaW9uPSIxLjEiIHhtbG5zPSJodHRwOi8vd3d3LnczLm9yZy8yMDAwL3N2ZyIgeG1sbnM6eGxpbms9Imh0dHA6Ly93d3cudzMub3JnLzE5OTkveGxpbmsiIHZpZXdCb3g9IjAsMCwxMDI0LDEwMjQiPgoJPGRlc2M+ZmlsZV9kb3dubG9hZCBpY29uIC0gTGljZW5zZWQgdW5kZXIgQXBhY2hlIExpY2Vuc2UgdjIuMCAoaHR0cDovL3d3dy5hcGFjaGUub3JnL2xpY2Vuc2VzL0xJQ0VOU0UtMi4wKSAtIENyZWF0ZWQgd2l0aCBJY29uZnUuY29tIC0gRGVyaXZhdGl2ZSB3b3JrIG9mIE1hdGVyaWFsIGljb25zIChDb3B5cmlnaHQgR29vZ2xlIEluYy4pPC9kZXNjPgoJPGcgZmlsbD0iI2ZmZmZmZiIgZmlsbC1ydWxlPSJub256ZXJvIiBzdHlsZT0ibWl4LWJsZW5kLW1vZGU6IG5vcm1hbCI+CgkJPHBhdGggZD0iTTUxMiw2ODIuNjdsLTI5OC42NywtMjk4LjY3aDE3MC42N3YtMjU2aDI1NnYyNTZoMTcwLjY3ek04MTAuNjcsNzY4djg1LjMzaC01OTcuMzR2LTg1LjMzeiIvPgoJPC9nPgo8L3N2Zz4=)](https://github.com/tarkovtracker-org/RatScanner/releases/latest/download/RatScanner.zip)

External item scanner for [Escape from Tarkov][escape-from-tarkov]. Screenshots identify items; pricing and related data come from the [tarkov.dev][tarkov-dev] API. This fork deepens [TarkovTracker.org][tarkovtracker] integration and continues active development.

**Support / issues for this build:** [TarkovTracker Discord][discord] · [this GitHub repo][fork-repo]
Demo: [Tutorial video][demo-video] · [FAQ][faq-page]

## Can I get banned for using Rat Scanner?

Battlestate Games does not support or affiliate with this project. The original tool has been used by many players for years without proven bans. Use at your own risk.

## What it does

Scan items in-game and view average price, value per slot, and (when connected) quest / hideout relevance.

## How it works

Entirely external — no game memory access. A screenshot is taken, image processing identifies the item, and results appear in the window and overlay tooltip.

## How to use

Game may need `Borderless` or `Windowed` mode for the overlay.

### Name scanning

_Scan the inspection name of an item._

- Left-click the magnifier icon in the inspect window

Limitations:

- Uses / durability assumed at 100%
- Weapons and modable items only show the base item

![Name scanning demo](media/NameScan.gif)

### Icon scanning

_Scan the icon of an item._

- Hold the modifier key while left-clicking (default `Shift`; changeable in settings)

Limitations:

- Weapons can no longer be scanned by icon
- Uses / durability assumed at 100%
- Shared icons (especially keys) can match incorrectly
- Stash lighting can interfere with the top-left stash area

![Icon scanning demo](media/IconScan.gif)

## Minimal UI

Title bar button switches to minimal UI. **Double-click** the window to return. Opacity and fields are configurable in settings.

![Minimal UI how-to demo](media/MinimalUI-HowTo.gif)

## Download

Get the [latest RatScanner.zip from this fork][latest-release] or a [specific release][releases].

Extract and run `RatScanner.exe`. Confirm resolution in settings (default Full HD).

Help: [FAQ][faq-page] · [TarkovTracker Discord][discord] · [common start issues][common-issues]

## Setting up the repository for development

Requirements: **64-bit Windows**, [.NET 10 SDK](https://dotnet.microsoft.com/download).

1. Clone **this** repo: `https://github.com/tarkovtracker-org/RatScanner`
2. From the repo root, run **`dev.bat`** (downloads icons/OCR data and restores packages on first run)

### Day-to-day coding

| What you want | Command |
| --- | --- |
| **Auto rebuild + restart on save** | `dev.bat` |
| Run once | `dev.bat -Once` |
| Re-download icons/OCR data | `dev.bat -ForceSetup` |
| Release config (local loop) | `dev.bat -Release` |

`dev.bat` ensures `src\App\Data\`, restores packages, then runs **`dotnet watch`** (restart-on-save). WPF hot reload is limited; that loop is the reliable workflow.

### Repository layout

```text
src/App/           # Main WPF app
src/ScanEngine/    # Scan engine (historical RatEye; in-tree)
tests/             # Unit tests
scripts/           # dev + data setup
```

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup-data.ps1
dotnet restore RatScanner.sln
dotnet watch --project src\App\RatScanner.csproj --non-interactive --no-hot-reload run
dotnet run --project src\App\RatScanner.csproj
```

Or open `RatScanner.sln` and press **F5**.

### Compiling only

```bat
dotnet build RatScanner.sln
```

### Tests and formatting

```bat
dotnet test RatScanner.sln
dotnet tool restore
dotnet csharpier check .
```

### Publishing (slow; not for day-to-day)

```bat
publish.bat
```

Output: `publish\RatScanner.exe`, `RatScanner.zip` (includes `LICENSE`). Prefer **`dev.bat`** while coding.

## Contributing

See `CONTRIBUTING.md`. PRs and issues: **[tarkovtracker-org/RatScanner][fork-repo]**.

Default integration branch is **`master`**. Day-to-day work uses short-lived `feat/…` / `fix/…` branches and PRs against the fork.

**Agent / contributor architecture docs:** root [`AGENTS.md`](AGENTS.md) (control plane) and [`docs/agent-context/`](docs/agent-context/README.md) (focused context). Nested `AGENTS.md` files under `src/App`, `src/ScanEngine`, and `tests` apply path-scoped rules. Implementation and project files override stale documentation.

## Community & support (this build)

Maintained by **[TarkovTracker.org][tarkovtracker]** — integration and ongoing development of this fork.

| | |
| --- | --- |
| Site | [https://tarkovtracker.org][tarkovtracker] |
| Source & releases | [github.com/tarkovtracker-org/RatScanner][fork-repo] |
| Chat / help | [discord.gg/M8nBgA2sT6][discord] |
| Market data | [tarkov.dev][tarkov-dev] |

## Attribution & license

**This software has been modified** from the original RatScanner project.

Originally created by **Moritz / Blightbuster**. Historical source: [github.com/RatScanner/RatScanner][original-repo].

Current development of **this** build is by TarkovTracker.org. For help with **this** version, use the [TarkovTracker Discord][discord] or [this repository][fork-repo] — not the original author or upstream repo.

Terms are in [`LICENSE`](LICENSE) (based on Elastic License 2.0). In short:

- Use, copy, and distribute under those terms.
- **Do not** sell, rent, or commercially distribute the software.
- Anyone who receives a copy must also receive the license terms (`LICENSE` is included with releases).
- Modified copies must include a **prominent notice that the software was modified** (this section and the in-app About page).
- Do not remove or obscure licensing notices shipped with the software.

Battlestate Games is not affiliated with this project.

---

[common-issues]: FAQ.md#program-issues
[demo-video]: https://www.youtube.com/watch?v=tXoIkgXFmdA
[discord]: https://discord.gg/M8nBgA2sT6
[escape-from-tarkov]: https://www.escapefromtarkov.com/
[faq-page]: FAQ.md
[fork-repo]: https://github.com/tarkovtracker-org/RatScanner
[latest-release]: https://github.com/tarkovtracker-org/RatScanner/releases/latest/download/RatScanner.zip
[original-repo]: https://github.com/RatScanner/RatScanner
[releases]: https://github.com/tarkovtracker-org/RatScanner/releases/
[tarkov-dev]: https://tarkov.dev/
[tarkovtracker]: https://tarkovtracker.org
