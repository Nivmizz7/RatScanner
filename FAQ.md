# Rat Scanner FAQ

**TarkovTracker Edition** — maintained by [TarkovTracker.org](https://tarkovtracker.org)

[![GitHub](https://img.shields.io/badge/GitHub-tarkovtracker--org%2FRatScanner-181717?style=for-the-badge&logo=github)](https://github.com/tarkovtracker-org/RatScanner)
[![Discord](https://img.shields.io/badge/Discord-TarkovTracker-7389D8?style=for-the-badge&logo=discord&logoColor=ffffff&labelColor=6A7EC2)](https://discord.gg/M8nBgA2sT6)

![Rat Scanner logo](media/RatLogo.png)

This is a **modified** community fork. Support: [TarkovTracker Discord](https://discord.gg/M8nBgA2sT6) · [this repo](https://github.com/tarkovtracker-org/RatScanner). Original project attribution is in the [README](README.md#attribution--license).

## Table of Contents

- General
  - [Can I get banned for using Rat Scanner?](#can-i-get-banned-for-using-rat-scanner)
- Program issues
  - [There is no RatScanner.exe file](#there-is-no-ratscannerexe-file)
  - [Rat Scanner is not starting](#rat-scanner-is-not-starting)
  - [Nothing happens when scanning](#nothing-happens-when-scanning)
  - [RatUpdater.exe could not be found! Please update manually](#ratupdaterexe-could-not-be-found-please-update-manually)
  - [Unable to download updater, please update manually](#unable-to-download-updater-please-update-manually)
  - [Could not find icon cache folder at](#could-not-find-icon-cache-folder-at)
  - [Could not find dynamic correlation data at](#could-not-find-dynamic-correlation-data-at)
  - [The type initializer for 'OpenCvSharp.NativeMethods' threw an exception](#the-type-initializer-for-opencvsharpnativemethods-threw-an-exception)
  - [The system cannot find the file specified](#the-system-cannot-find-the-file-specified)
- Scanning issues
  - [Icon scanning gets a lot of wrong matches](#icon-scanning-gets-a-lot-of-wrong-matches)

---

## General

### Can I get banned for using Rat Scanner?

While Battlestate Games does not support nor is affiliated with this project, it exists since two years with over 1.000 players using it every day in their games. So far there has not been a single instance in which RatScanner was proven to have caused any ban.

---

## Program issues

### There is no RatScanner.exe file

Make sure you downloaded and extracted the files as described inside the [download section][download-section]

If you still cannot see `RatScanner.exe` it is most likely removed by your antivirus.
In that case, create a exception for it or disable your antivirus.

### Rat Scanner is not starting

- Try starting `RatScanner.exe` as administrator
- Make sure that there is no antivirus blocking RatScanner from accessing or downloading additional files

If you still can't run the application, you are probably missing the WebView2 Runtime (which should usually come with Edge).

[Download WebView2 Runtime][webview2-download]

### Nothing happens when scanning

- Check that you set your resolution correctly inside the settings
- Try to run RatScanner as administrator
- Try to disable HDR

### RatUpdater.exe could not be found! Please update manually

Download the latest version from the [RatScanner releases page][ratscanner-latest].

### Unable to download updater, please update manually

Download the latest version from the [RatScanner releases page][ratscanner-latest].

### Could not find icon cache folder at

Please have a look at [Could not find dynamic correlation data at](#could-not-find-dynamic-correlation-data-at).

### Could not find dynamic correlation data at

1. Close RatScanner
2. Start Escape From Tarkov
3. Go to Mechanics trading screen and wait for all icons to load (no spinning circles)
4. Start RatScanner.exe

### The type initializer for 'OpenCvSharp.NativeMethods' threw an exception

This probably means you are missing the Windows Media Features.

There are two ways to install the Media Feature Pack:

- Navigate to **Settings** > **Apps** > **Apps and features** > **Optional features** > **Add a feature**, and then locate **Media Feature Pack** in the list of available optional features.
- Download the [Windows Media Feature Pack][windows-media-pack] installer for your Windows version and run it.

After the installation has finished, restart your computer to make sure the changes are applied.

### The system cannot find the file specified

Uninstall WebView2 through windows `Add or remove programs` interface.

If you can't uninstall it, search in windows for regedit and delete these two entries (or make sure they dont exist):

- `HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`
- `HKEY_CURRENT_USER\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`

After you've uninstalled it (or removed the entries), restart your pc and install the [WebView2 Runtime][webview2-download].

---

## Scanning issues

### Icon scanning gets a lot of wrong matches

Icon scanning still has some known issues, some which are not possible to fix.
This currently leads to items like keys and small attachments matching wrong due to their similarity to other items.
Also, when in the stash, the bright light in the top center of the screen interferes with the top left section of the stash which results in extremely bad results.

[download-section]: https://github.com/tarkovtracker-org/RatScanner#download
[ratscanner-latest]: https://github.com/tarkovtracker-org/RatScanner/releases/latest/download/RatScanner.zip
[webview2-download]: https://go.microsoft.com/fwlink/p/?LinkId=2124703
[windows-media-pack]: https://www.microsoft.com/en-us/software-download/mediafeaturepack
