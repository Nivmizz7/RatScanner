# Rat Scanner

![RatScanner logo](media/RatLogo.png)

**TarkovTracker Edition** — community-maintained fork by [TarkovTracker.org][tarkovtracker].

[![GitHub](https://img.shields.io/badge/GitHub-tarkovtracker--org%2FRatScanner-181717?style=for-the-badge&logo=github)](https://github.com/tarkovtracker-org/RatScanner)
[![Discord](https://img.shields.io/badge/Discord-TarkovTracker-7389D8?style=for-the-badge&logo=discord&logoColor=ffffff&labelColor=6A7EC2)](https://discord.gg/M8nBgA2sT6)
[![Download](https://img.shields.io/static/v1?&label=&message=Download&color=4FBD54&style=for-the-badge&logo=data:image/svg+xml;base64,PHN2ZyB2ZXJzaW9uPSIxLjEiIHhtbG5zPSJodHRwOi8vd3d3LnczLm9yZy8yMDAwL3N2ZyIgeG1sbnM6eGxpbms9Imh0dHA6Ly93d3cudzMub3JnLzE5OTkveGxpbmsiIHZpZXdCb3g9IjAsMCwxMDI0LDEwMjQiPgoJPGRlc2M+ZmlsZV9kb3dubG9hZCBpY29uIC0gTGljZW5zZWQgdW5kZXIgQXBhY2hlIExpY2Vuc2UgdjIuMCAoaHR0cDovL3d3dy5hcGFjaGUub3JnL2xpY2Vuc2VzL0xJQ0VOU0UtMi4wKSAtIENyZWF0ZWQgd2l0aCBJY29uZnUuY29tIC0gRGVyaXZhdGl2ZSB3b3JrIG9mIE1hdGVyaWFsIGljb25zIChDb3B5cmlnaHQgR29vZ2xlIEluYy4pPC9kZXNjPgoJPGcgZmlsbD0iI2ZmZmZmZiIgZmlsbC1ydWxlPSJub256ZXJvIiBzdHlsZT0ibWl4LWJsZW5kLW1vZGU6IG5vcm1hbCI+CgkJPHBhdGggZD0iTTUxMiw2ODIuNjdsLTI5OC42NywtMjk4LjY3aDE3MC42N3YtMjU2aDI1NnYyNTZoMTcwLjY3ek04MTAuNjcsNzY4djg1LjMzaC01OTcuMzR2LTg1LjMzeiIvPgoJPC9nPgo8L3N2Zz4=)](https://github.com/tarkovtracker-org/RatScanner/releases/latest/download/RatScanner.zip)

Rat Scanner is an external tool for [Escape from Tarkov][escape-from-tarkov] that scans items from screenshots and shows pricing, quest, and hideout-related info using the [tarkov.dev][tarkov-dev] API.

This repository is a **modified fork**. It is maintained by TarkovTracker for deeper TarkovTracker.org integration and active development without putting that load on the original author.

[Tutorial / Demo Video][demo-video] · [FAQ][faq-page]

## Attribution & license

Originally created by **Moritz / Blightbuster** — [original RatScanner repository][original-repo].

This software is modified from that work. See [`LICENSE`](LICENSE) (Elastic License 2.0–based). Important terms in plain language:

- You may use, copy, and distribute the software under the license terms.
- Commercial resale / selling the software is prohibited.
- Anyone you give a copy to must also get the license terms.
- Modified copies must include a clear notice that the software was modified (this README and the in-app credits serve that purpose).

Item market data is provided by [tarkov.dev][tarkov-dev]. Battlestate Games is not affiliated with this project.

## Can I get banned for using Rat Scanner?

While Battlestate Games does not support nor is affiliated with this project, the original tool has existed for years with many players using it daily. So far there has not been a single instance in which RatScanner was proven to have caused any ban. Use at your own risk.

## What it does

Rat Scanner lets you scan items in-game and shows data such as average price, value per slot, and (when connected) quest / hideout relevance.

## How it works

The tool is entirely external. It does not read game memory.

When you scan an item, a screenshot is taken and image processing identifies the item. The result is looked up in the database and shown in the main window and overlay tooltip.

## How to use

Your game may need to be in either `Borderless` or `Windowed` mode for the overlay to work.

### Name scanning

_Name scanning refers to scanning the inspection name of an item._

- Left-click the magnifier icon inside the inspect window

Limitations:

- Uses / durability is always assumed at 100%
- Weapons and other modable items only show info for the base item

![Name scanning demo](media/NameScan.gif)

### Icon scanning

_Icon scanning refers to scanning the icon of an item._

- Hold the modifier key while left-clicking an item
- The modifier key can be changed in settings (default is `Shift`)

Limitations:

- Weapons can no longer be scanned by icon
- Uses / durability is always assumed at 100%
- Items that share icons (especially keys) can match incorrectly
- Stash lighting in the top center of the screen can interfere with the top-left stash area

![Icon scanning demo](media/IconScan.gif)

## Minimal UI

Switch to the minimal UI via the title bar button. Return to the standard view by **double-clicking** inside the window.

Background opacity and which fields are shown can be configured in settings.

![Minimal UI how-to demo](media/MinimalUI-HowTo.gif)

## Download

Download the [latest RatScanner.zip from this fork][latest-release] or pick a [specific release][releases].

Extract the archive and run `RatScanner.exe`.

In settings, confirm your resolution (default is Full HD).

Problems? See the [FAQ][faq-page] or join the [TarkovTracker Discord][discord].

**Important:** If the tool does not seem to start, check [common issues][common-issues].

## Setting up the repository for development

Requirements: **Windows**, [.NET 10 SDK](https://dotnet.microsoft.com/download).

1. Clone **this** repository: `https://github.com/tarkovtracker-org/RatScanner`
2. From the repo root, run **`dev.bat`** (first run downloads icons/OCR data and restores packages)

### Day-to-day coding

| What you want | Command |
|---|---|
| **Auto rebuild + restart on save** | `dev.bat` |
| Run once (no watcher) | `dev.bat -Once` |
| Re-download icons/OCR data | `dev.bat -ForceSetup` |
| Release config (local debug loop) | `dev.bat -Release` |

`dev.bat` wraps `scripts\dev.ps1` and will:

1. Ensure `src\App\Data\` has icons + OCR data (`scripts\setup-data.ps1`)
2. Restore NuGet packages if needed
3. Run **`dotnet watch run`** so each save rebuilds and restarts the app

**Hot reload:** this is a WPF desktop app. True in-process hot reload is limited; restart-on-save is the reliable loop.

### Repository layout

```
src/App/           # Main WPF app
src/ScanEngine/    # Scan / image-processing engine (historical RatEye; in-tree)
tests/             # Unit tests
scripts/           # dev + data setup
```

Manual equivalents:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup-data.ps1
dotnet restore RatScanner.sln
dotnet watch --project src/App run --non-interactive
dotnet run --project src/App
```

Or open `RatScanner.sln` in Visual Studio / Rider and press **F5**.

### Compiling only

```sh
dotnet build RatScanner.sln
```

### Publishing (release package — slow; not for iteration)

```sh
publish.bat
```

- Output: `publish\RatScanner.exe` and `RatScanner.zip`
- Use **`dev.bat`** for normal coding

## Contributing

Please read `CONTRIBUTING.md` before contributing. PRs and issues go to **[tarkovtracker-org/RatScanner][fork-repo]**.

## Community & support

This fork is maintained by **[TarkovTracker.org][tarkovtracker]** for integration and continued development.

- App / site: [https://tarkovtracker.org][tarkovtracker]
- Source & releases: [github.com/tarkovtracker-org/RatScanner][fork-repo]
- Chat / help: [TarkovTracker Discord][discord]

Market data: [tarkov.dev][tarkov-dev].

## Links

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
