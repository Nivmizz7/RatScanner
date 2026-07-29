# Backlog

Working notes for issues found but not yet fixed. Append new items under the matching category. Strike through or remove when done.

## UI/UX

### Manage-key link points to home page and duplicates across PVP/PVE

**Status:** Not started
**Files:** `src/App/Constants.cs`, `src/App/Pages/App/Settings/SettingsTracking.razor`, `src/App/Components/ChangeConnectionDialog.razor`

The "Create or manage a TarkovTracker.org API key" / "...TarkovTracker.io key" links in Tracking settings point at the site home pages, not the API-key settings pages:

- Org link uses `Constants.Links.TarkovTracker` = `https://tarkovtracker.org` — should be `https://tarkovtracker.org/settings#api`.
- Io link uses `Constants.Links.TarkovTrackerIo` = `https://tarkovtracker.io` — should be `https://tarkovtracker.io/settings`.

`Constants.Links.TarkovTracker` is also used by `Credits.razor:31` for a "site" button that **should** stay the home page, so do not repurpose the existing constant. Add new `TarkovTrackerSettings` / `TarkovTrackerIoSettings` constants and point the manage-key links at those.

The link renders in three places, all currently using the home-page constants:

- `SettingsTracking.razor:148` — PVP unconfigured inline form, Io branch.
- `SettingsTracking.razor:187` — unconfigured inline form, Org branch (PVP-Org or PVE).
- `SettingsTracking.razor:522-524` — `OpenChangeConnectionDialogAsync` builds `manageUrl` for the configured-state `ChangeConnectionDialog`.

**Duplication problem:** the inline link lives inside `@if (!IsModeConfigured(mode))` within the `foreach (GameMode mode in Modes)` loop over `[Regular, Pve]`. When **both** modes are unconfigured **and** the PVP draft source is `Org`, the identical Org link renders under the PVP block **and** under the PVE block. When PVP draft is `Io`, the two blocks show different links (Io under PVP, Org under PVE) so there is no duplicate.

Edge case for any "show once" fix: if PVE is configured and PVP is unconfigured with Org draft, PVE's block shows no link (it's configured), so suppressing the PVP Org link would leave PVP with no manage-key link at all.

**Suggested fix shape:** add the two `…Settings` constants; point all three call sites at them; dedup by either (a) moving the link into a single footer after the mode loop that picks the right URL per current draft/configured state, or (b) keeping it inline but suppressing the PVP Org link only when PVE is also unconfigured with the same Org link.

### About page spacing is inconsistent

**Status:** Not started
**Files:** `src/App/Pages/App/Credits.razor`, `src/App/Pages/App/Credits.razor.css`

Vertical rhythm between text and button grids in the about cards is inconsistent. Observed from `Credits.razor.css`:

- `.about-card-title` margin `0 0 4px`.
- `.about-muted` margin `0 0 6px`.
- `.about-body` margin `0 0 10px`.
- `.about-modified` margin-top `8px`, **no bottom margin** — so the gap to the next card relies on card padding/margin only.
- `.about-license` margin-top `12px`.
- No explicit gap between the last text element and the `.about-btn-grid` that follows it within a card; spacing depends on whichever text class happened to be last.

The first card (`Credits.razor:12-18`) stacks title → muted → body → muted → modified with mixed 4/6/10/8px gaps and no bottom margin on the final modified notice. The button-grid cards have `.about-body` (`0 0 10px`) before the grid, which is a different gap than the title-only cards. Needs a consistent vertical-rhythm scale (e.g. uniform `--rs-space-*` tokens) applied to all card children.

## Versioning

### ~~Version should reflect beta status~~

**Status:** Done
**File:** `src/App/RatScanner.csproj`, `.github/workflows/release.yml`

Resolved: the product version is now `4.0.1-beta.1`, and the release workflow requires a prerelease suffix for the testing channel and publishes that channel as a GitHub prerelease.

## Release readiness / data integration

### Pin and verify the maintained RatScannerData release

**Status:** Done
**Files:** `scripts/RatScannerData.ps1`, `scripts/setup-data.ps1`, `publish.bat`, `.github/workflows/build.yml`

RatScanner's setup and packaging paths downloaded `RatScanner/RatScannerData/releases/latest`, so builds ignored the maintained `tarkovtracker-org/RatScannerData` bundle and could change when an unrelated upstream `latest` release moved. The current known-good input is `data-f1f047dc5d38ee43` with archive SHA-256 `bce49e8bc7dde57ad46fb95010627831d4483db2273d554e3add6c49388a3b38`.

The fix must keep one pinned release contract and make setup, local publish, and CI all verify:

- `Data.zip.sha256` matches both the downloaded archive and the repository pin;
- the published and embedded `manifest.json` files are identical and use schema version 1;
- required map, fallback image, and English OCR files exist;
- the extracted icon count matches the manifest and remains above a conservative sanity floor.

The existing build artifact must be rebuilt after this change; an artifact produced through the old URL must not be promoted as `4.0.1-beta.1`.

### Complete the manual beta scan and clean-machine gates

**Status:** Not started
**Files:** `tests/RatScanner.Tests/ScanPipelineImageHarnessTests.cs`, `src/ScanEngine/RatEye.Benchmarks`, `docs/agent-context/build-and-validation.md`

Hermetic CI intentionally skips private/current-game screenshot fixtures, so green unit tests do not prove current EFT recognition accuracy. Before beta promotion, replay the available diagnostic fixtures and exercise the exact packaged ZIP on clean Windows x64 for startup, RatEye native dependency loading, one name scan, highlighted and normal inventory scans, and at least one non-English OCR scan.

Track the durable benchmark/fixture work in [RatScanner issue #4](https://github.com/tarkovtracker-org/RatScanner/issues/4) rather than creating duplicate issues for each scan case.

### Repair or remove the obsolete RatEye tagged-release workflow

**Status:** Not started
**Files:** `tarkovtracker-org/RatEye/.github/workflows/tagged-release.yml`

RatEye's normal .NET 10 build passes, but its separate tagged-release workflow still uses obsolete action versions and .NET 7, runs on inappropriate push triggers, and currently fails before starting a job. This does not block RatScanner's source-submodule runtime, but it blocks a clean independent/upstream-ready RatEye release story. Track this as one RatEye repository issue after the RatScanner data fix is validated.

## Other

<!-- Append non-UI, non-versioning items here. -->

## Template for new items

```text
### Short title

**Status:** Not started | In progress | Blocked
**Files:** path/to/file, ...

Describe the problem with evidence (file:line, observed behavior, expected behavior).
Note edge cases and suggested fix shape. Keep it scoped so it can be picked up
without re-investigation.
```
