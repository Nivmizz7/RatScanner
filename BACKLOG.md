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

### Version should reflect beta status

**Status:** Not started
**File:** `src/App/RatScanner.csproj:17` (`<Version>4.0.0</Version>`)

Product version is `4.0.0` with no prerelease tag. Per `AGENTS.md` the `<Version>` lives only here. To mark the build as beta, use a semver prerelease suffix such as `4.0.0-beta` or `4.0.0-beta.1`.

Before changing, verify how `MenuVM.VersionDisplay` renders the version (whether it strips/handles prerelease tags) and whether `publish.bat` / CI release tagging on `v*` tags needs to match the prerelease shape. Update `docs/agent-context/release-and-versioning.md` in the same change set if the versioning scheme changes.

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
