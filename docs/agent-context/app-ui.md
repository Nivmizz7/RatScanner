# App UI

Scoped mandatory rules also live in `src/App/AGENTS.md`.

## WPF / Blazor hybrid relationship

- **WPF** owns OS windows, chrome, tray, jump list, single-instance activation, and hosts `BlazorWebView` controls.
- **Blazor** owns almost all product UI (scan page, recent scans, settings, about, overlays).
- Host pages:
  - Main: `wwwroot/index.html` → `RazorApp` → routes under `/app`
  - Overlay tooltip: `wwwroot/overlay.html` → `/overlay`
- The passive overlay WebView initializes in a small, non-topmost bootstrap window. After initialization, its native window is hidden whenever no unexpired tooltip exists and expands into the topmost virtual-screen overlay only for the configured tooltip lifetime, so startup and idle operation do not leave a monitor-sized topmost surface above the Windows taskbar or game.
- Each `BlazorWebView` sets its route with `StartPath`. Do not replace this with `NavigateTo` from a root component: all routers scan the same assembly, so an initial `/` render leaks the main app page into transparent overlay windows.
- Icons/static Data files are exposed to WebView via virtual host `local.data` → `Data/`.

Do not invent a second navigation stack in WPF for features that already live as Blazor routes.

## MudBlazor usage

- Registered in DI via `AddMudServices()` in `BlazorUI`.
- `MainLayout` provides `MudThemeProvider` (dark via `IsDarkMode` + `PaletteDark`), `MudPopoverProvider`, `MudDialogProvider`, and `MudSnackbarProvider`. **All four providers are required** (popover was mandatory since MudBlazor 7).
- `AddMudServices()` also registers Mud popover/overlay services used by autocomplete, menus, and related components — do not fork a second Mud services pipeline.
- Theme is a dark `MudTheme` in `MainLayout.razor` (primary purple aligned with CSS variables). Use `PaletteDark` / `DefaultTypography` (not legacy `Palette` / `Default`).
- Prefer Mud component **parameters** (`Margin`, `Variant`, `Underline`, `FullWidth`, density) over fighting component chrome with CSS.
- Bind checkboxes/switches with `@bind-Value` (not legacy `@bind-Checked`). Custom field converters implement `IConverter` / `Conversions.From`.
- MudBlazor analyzers (e.g. MUD0002 illegal attributes) should be treated as defects when they fire.

Package version: see App `.csproj` only.

## Layout and component ownership

| Area | Location |
| --- | --- |
| App shell (collapsible sidebar, PVP/PVE selector) | `Shared/AppLayout.razor(+.css)` |
| Mud providers / shared theme | `Shared/MainLayout.razor` |
| Settings chrome | `Shared/SettingsLayout.razor(+.css)` |
| Overlay shell | `Shared/OverlayLayout.razor`, passive tooltip page |
| Scanner status chip | `Shared/ScannerStatus.razor(+.css)` |
| Primary scan / search UI | `Pages/App/Index.razor(+.css)` |
| Settings pages | `Pages/App/Settings/*` |
| Scan tooltip overlay | `Pages/Overlay/*` |

Presentation logic for results: `Presentation/*` and `ViewModel/*`.

## Theme ownership

| Layer | Owner |
| --- | --- |
| Mud palette / typography | `MainLayout` `_theme` |
| Design tokens + global layout helpers | `wwwroot/css/theme.css` (`--rs-*` CSS variables) |
| Page-specific rules | co-located `*.razor.css` (scoped) |
| WPF title bar native brushes | `App.xaml` + `PageSwitcher.ApplyWindowsTheme` |

Keep Mud palette and CSS variables roughly aligned when changing brand colors.

## CSS organization

1. **Global theme** — `theme.css` linked from host HTML after MudBlazor CSS.
2. **Scoped component CSS** — Blazor CSS isolation (`Component.razor.css`).
3. **Bundled** — `RatScanner.styles.css` generated for isolation.

### Specificity and `!important`

Order of preference:

1. MudBlazor / component parameters
2. Structure and class hierarchy
3. More specific selectors
4. `!important` only when truly required (e.g. accessibility `prefers-reduced-motion` overrides in theme, or irreducible third-party conflicts)

Do not use `!important` to paper over conflicting selectors. Recent search-field work deliberately avoided height `!important` by adjusting Mud input padding/metrics.

## Accessibility, keyboard, and focus

- `:focus-visible` outline tokens live in `theme.css`.
- Prefer `aria-label` on icon-only controls (see `AppLayout` nav toggle/scrim).
- Main search focus shortcut: Ctrl/Cmd+K (`index.html`).
- Reduced motion: respect existing `@media (prefers-reduced-motion: reduce)` rules; do not reintroduce long mandatory animations.

## Compact, aligned, consistent controls

- Prefer shell density already established in `AppLayout` / search chrome (single-line status chip, natural Mud input height).
- Stretch full-width inputs with Mud props / flex, not forced content-box height hacks.
- Keep scanner status and search co-located patterns coherent when editing the scan page.

## Sidebar collapse behavior

`AppLayout` exposes a single collapsible sidebar that works at every viewport width; the native WPF title bar (`PageSwitcher.xaml`) supplies app identity, window drag, and caption buttons at all widths, so the Blazor shell no longer renders a duplicate compact header. The only toggle is the WPF title-bar `NavToggleButton`, which routes through `AppStateService` (`SidebarToggleRequested`) — there is no sidebar-header collapse button and no floating expand button in the Blazor shell.

- The narrow/docked breakpoint is **680px**, applied consistently by `wwwroot/index.html` (`matchMedia("(max-width: 680px)")`), `AppLayout.razor.css`, `Index.razor.css`, and `theme.css`.
- Sidebar state is one of four discrete names reported to JS via `RatScanner.setSidebarState` (see `AppLayout.GetSidebarStateName`): `expanded` (desktop, docked full width), `rail` (desktop, collapsed icon rail), `narrow-open` (overlay drawer), `narrow-closed` (overlay hidden). At desktop widths `main-content` reserves `--rs-sidebar-width` via the `sidebar-docked` class and the sidebar is non-modal; below 680px the sidebar is an overlay drawer with a scrim.
- `--rs-sidebar-active-width` on `:root` is kept in sync by `wwwroot/index.html` from the current state name so MudBlazor dialogs center in the actual content pane. `AppLayout` registers a `DotNetObjectReference` to receive breakpoint crossings via `OnViewportNarrow` and drawer-close requests via `CloseDrawerFromJs`.
- While the narrow drawer is open, `<main>` gets `aria-hidden="true"`, CSS `pointer-events: none`, and `inert` (splat via `AppLayout.MainAttributes`, recomputed each render); a JS Tab-trap (plus Escape-to-close) in `index.html` keeps focus inside the sidebar.

## Visual smoke-test surfaces

After material UI changes, manually verify at least:

1. Main `/app` scan page (search + status + results).
2. Settings general page (language / display controls if touched).
3. Settings advanced page (advanced capture / diagnostics / detected configuration if touched).
4. Sidebar navigation open/close on narrow window.
5. Optional: DPI scale / multi-monitor if display logic changed.

## Screenshot acquisition (UI-adjacent)

Capture is **not** in Razor; UI only reflects scan state. Capture orchestration: `RatScannerMain` + display services under `Display/`. Settings UI edits game display preferences that feed capture scale.

## Configuration model (UI surface)

Settings use control-specific persistence through `SettingsVM` and `SettingsPersistenceService` into `RatConfig` / `config.cfg`; there is no page-level Save or Cancel bar. Complete choices (switches, selects, presets, game mode) apply immediately and are saved asynchronously with per-setting rollback on failure. Editable capture fields keep draft text, validate on blur/Enter, and persist only when valid. TarkovTracker credentials are an explicit exception: they remain local drafts until a successful connection test.

The always-visible PVP/PVE selector in `AppLayout` switches mode-specific tarkov.dev caches and the matching TarkovTracker.org progress context immediately, then persists the selection. Stale tracker requests are canceled or rejected by configuration generation before they can overwrite the active mode.

## Package ownership (UI-related)

| Package family | Project |
| --- | --- |
| WebView.Wpf, MudBlazor, Win Compatibility, SingleInstanceCore | App |
| Scan/vision packages for processing | Standalone RatEye submodule (plus App for Tesseract where referenced) |

Do not move MudBlazor into RatEye.

## Practical UI change checklist

1. Read this file + `src/App/AGENTS.md`.
2. Match nearby Razor patterns and localization keys.
3. Prefer props over CSS force; keep theme.css coherent.
4. Build; run WebView smoke for non-trivial layout.
5. Update i18n for any new user-visible strings.
