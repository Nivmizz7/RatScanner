---
name: dotnet-project
description: Implement and verify RatScanner work involving C#, .NET, solution or project files, refactoring, backend or API behavior, dependencies, tests, configuration, data/cache behavior, WPF, Blazor WebView UI, and bug fixes. Use for any code change owned by this repository, including UI changes that require real WebView2 validation.
---

# Work on RatScanner .NET

## Establish context

1. Read the root `AGENTS.md` and the routed `docs/agent-context/` files before editing.
2. Read the closest nested `AGENTS.md` under `src/App`, `src/ScanEngine`, or `tests`.
3. Inspect `git status`, the relevant project files, and adjacent tests. Preserve unrelated work.
4. Keep the Windows x64 and standalone RatEye submodule boundaries intact.

## Respect the architecture

- Treat `src/App` as the WPF host and Blazor WebView product UI. WPF owns native chrome/lifecycle; Blazor and MudBlazor own most screens. Read the project file for the current target framework.
- Treat `src/ScanEngine` as the independently owned RatEye submodule. RatEye must not reference RatScanner; make engine changes in its source repository when required.
- Keep App-owned unit tests in `tests/RatScanner.Tests`. Keep real hosted-UI smoke tests in `tests/RatScanner.UiTests`.
- Use `TarkovDevAPI` for bulk catalog access and preserve its cache/rate-limit behavior. Keep secrets in local `config.cfg`, never source control.
- Keep the product version only in `src/App/RatScanner.csproj`.

## Follow repository conventions

- Keep App nullable annotations sound and explicit; do not suppress nullability to finish a change.
- Keep explicit `using` directives because App disables implicit usings.
- Propagate `CancellationToken` through async I/O and test APIs when a caller token exists. Avoid blocking the WPF dispatcher.
- Use the existing static/singleton composition where it is authoritative; do not introduce a competing DI architecture during an unrelated change.
- Log recoverable runtime failures with `Logger.LogWarning`; reserve fatal handling for unrecoverable product failure. Preserve cleanup/disposal on every lifecycle path.
- Use `RatConfig` and the existing settings persistence services for configuration. Do not write credentials or destructive test state into production resources.
- Follow nearby JSON/Newtonsoft, API, validation, EF-free persistence, and naming patterns rather than importing generic conventions.
- Prefer MudBlazor parameters and semantic HTML/ARIA over CSS force. Update every locale file when adding a user-visible string.
- Use xUnit v3 and deterministic, hermetic assertions. Do not add another test framework. Put RatEye image-processing tests in RatEye; do not mock away the behavior being proved.
- Reuse existing dependencies. Explain any new production or test dependency, choose a stable published version, and avoid broad upgrades.

## Verify proportionately

Run commands from the repository root.

| Change | Minimum fresh evidence |
| --- | --- |
| Documentation or agent guidance | Markdown lint; `scripts/check-agent-docs.ps1`; validate a changed skill |
| Small isolated C# change | affected project build; targeted xUnit tests; CSharpier check |
| Normal App change | `scripts/verify.ps1 -Mode Fast` plus affected tests |
| Cross-cutting, project, dependency, or configuration change | `scripts/verify.ps1 -Mode Full` |
| RatEye processing change | RatEye build/tests in the submodule plus integrated RatScanner build; fixture/live scan evidence when accuracy changes |
| Razor, CSS, WPF/WebView host, navigation, or visible behavior | affected tests plus `scripts/verify.ps1 -Mode Ui`; inspect the retained screenshots |
| Publish/release change | Full verification plus the repository publish/package verifier |

Do not call work complete because it compiles. Report the exact commands, exit results, and behavior exercised.

## Validate visible changes

For visible UI behavior:

1. Build and run affected automated tests.
2. Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1 -Mode Ui` with no unrelated RatScanner instance open.
3. Exercise the changed flow in the real WPF-hosted WebView2 target using role/name/label locators where possible. Use stable route attributes only when a hidden narrow drawer makes its semantic link intentionally non-interactable.
4. Check the normal desktop viewport and each relevant narrow/responsive viewport. Assert overflow/bounds and inspect the PNGs under `artifacts/ui-tests/`.
5. Check keyboard activation/focus, important ARIA structure, console errors, uncaught page errors, failed app-resource requests, and HTTP 5xx responses.
6. Add or extend a durable UI test for critical repeatable behavior. Keep exploratory agent actions complementary.
7. On failure, inspect `failure.png`, `failure-dom.html`, `failure-accessibility.yml`, `browser-runtime.log`, `RatScanner.log`, and `trace.zip` in the newest artifact directory.

The harness attaches Playwright .NET to the app's installed WebView2 runtime over an isolated CDP profile. It does not need a Playwright browser download or Playwright MCP. RatScanner is single-instance: never stop an unrelated user process to make a test pass.

## Finish safely

- Review the diff for generated files, secrets, version drift, accidental submodule changes, and unrelated formatting.
- Update root/nested agent docs when commands, architecture, packages, paths, or verification policy changed.
- Distinguish unit, runtime, visual, and live-game evidence. State remaining hardware, game-client, external-service, DPI, or visual-judgment gaps explicitly.
