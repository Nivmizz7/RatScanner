# App UI

Scoped mandatory rules also live in `src/App/AGENTS.md`.

## WPF / Blazor hybrid relationship

- **WPF** owns OS windows, chrome, tray, jump list, single-instance activation, and hosts `BlazorWebView` controls.
- **Blazor** owns almost all product UI (scan page, history, settings, credits, overlays).
- Host pages:
  - Main: `wwwroot/index.html` → `RazorApp` → routes under `/app`
  - Overlay tooltip: `wwwroot/overlay.html`
  - Interactable overlay: `wwwroot/interactableOverlay.html`
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
| App shell (sidebar, header, status) | `Shared/AppLayout.razor(+.css)` |
| Mud providers / shared theme | `Shared/MainLayout.razor` |
| Settings chrome | `Shared/SettingsLayout.razor(+.css)` |
| Overlay shells | `Shared/OverlayLayout.razor`, overlay page trees |
| Scanner status chip | `Shared/ScannerStatus.razor(+.css)` |
| Primary scan / search UI | `Pages/App/Index.razor(+.css)` |
| Settings pages | `Pages/App/Settings/*` |
| Interactable search/maps | `Pages/InteractableOverlay/*` |
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
- Keep scanner status and search co-located patterns coherent when editing the header.

## Visual smoke-test surfaces

After material UI changes, manually verify at least:

1. Main `/app` scan page (search + status + results).
2. Settings general page (language / display controls if touched).
3. Sidebar navigation open/close on narrow window.
4. Optional: interactable overlay search.
5. Optional: DPI scale / multi-monitor if display logic changed.

## Screenshot acquisition (UI-adjacent)

Capture is **not** in Razor; UI only reflects scan state. Capture orchestration: `RatScannerMain` + display services under `Display/`. Settings UI edits game display preferences that feed capture scale.

## Configuration model (UI surface)

Settings pages bind through `SettingsVM` → `RatConfig` → `config.cfg`. Save/cancel flows must keep VM and disk config consistent; invalid tokens may clear tracking configuration.

## Package ownership (UI-related)

| Package family | Project |
| --- | --- |
| WebView.Wpf, MudBlazor, Win Compatibility, SingleInstanceCore | App |
| Scan/vision packages for processing | ScanEngine (plus App for Tesseract where referenced) |

Do not move MudBlazor into ScanEngine.

## Practical UI change checklist

1. Read this file + `src/App/AGENTS.md`.
2. Match nearby Razor patterns and localization keys.
3. Prefer props over CSS force; keep theme.css coherent.
4. Build; run WebView smoke for non-trivial layout.
5. Update i18n for any new user-visible strings.
