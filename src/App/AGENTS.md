# App (`src/App`) — scoped agent instructions

Read with root `AGENTS.md` and, before material work, the routed docs under `docs/agent-context/` (usually `architecture.md`, `app-ui.md`, `data-integrations.md`, `configuration-and-cache.md`, `localization.md`).

## Scope

WPF host, Blazor WebView UI, configuration, tarkov.dev/TarkovTracker clients, scan orchestration, presentation, i18n.

## Mandatory

1. **Windows x64-only** host assumptions (WPF, WebView2, WinForms tray/screens/DPI, x64 OpenCvSharp native runtime, DPAPI).
2. Reference scan engine via **ProjectReference** to `../ScanEngine/RatEye.csproj` only — never NuGet `RatEye`.
3. Bulk catalog I/O goes through **`TarkovDevAPI`**. Keep maps off cold-start critical path; slim GraphQL maps + JSON fallback is intentional.
4. Product **`<Version>`** lives only in `RatScanner.csproj`. Preserve TarkovTracker Edition branding (`Constants.Branding`, UA `RatScanner-TT/…`).
5. **UI styling:** prefer MudBlazor parameters and specificity; avoid new `!important` except clear a11y/third-party necessities. Global tokens: `wwwroot/css/theme.css`.
6. **User-visible strings:** add keys to all `i18n/*.json` files (`en` baseline). UI language ≠ OCR/API language.
7. **Secrets:** tracker tokens only in user config (secure fields); never commit tokens or raw cache dumps.
8. **Data assets:** `Data/**` is downloaded (gitignored). Do not commit icons/OCR dumps. Keep `Watch=false` on Data content items.
9. Dispose / single-instance lifecycle: honor existing `DisposeInstance` paths on exit; do not create unbounded WebView/service leaks.
10. Implicit usings are disabled — keep explicit `using` directives.
11. Implementation overrides this file; update it when App-scoped rules change.

## Prefer

- Match surrounding Razor/C# style; nullable correctness.
- Presentation helpers under `Presentation/` for scan result shaping.
- Existing DI registrations in `View/BlazorUI.xaml.cs` when adding Blazor services.
- Mud providers owned by `MainLayout` / `AddMudServices()` rather than ad-hoc second pipelines.

## Validate

`dotnet build` / `dotnet test` from repo root; WebView smoke via `dev.bat` for UI; i18n completeness for string changes. See `docs/agent-context/build-and-validation.md`.
