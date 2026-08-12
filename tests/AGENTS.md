# Tests (`tests/`) — scoped agent instructions

Read with root `AGENTS.md` and `docs/agent-context/build-and-validation.md` before adding or changing tests.

## Scope

- `tests/RatScanner.Tests`: hermetic xUnit unit/contract tests that reference App.
- `tests/RatScanner.UiTests`: Playwright .NET smoke tests against the real WPF-hosted WebView2.

Both use the App's Windows x64 TFM. UI tests launch a built RatScanner process with an isolated WebView2 profile and must own only that process tree.

## Mandatory

1. Tests must run through `scripts\verify.ps1` on Windows. Keep unit and UI execution separate so failures identify their layer.
2. Keep the unit suite **pure, hermetic, and deterministic** (no live network, game client, WebView, clipboard, or hotkeys). Keep UI tests deterministic and isolated while using the real WebView by design.
3. Do not commit secrets, large binary fixtures, or full Data icon dumps. If a screenshot/OCR fixture is essential, keep it small, document provenance in the test, and avoid copyright-sensitive game assets when possible.
4. Internals under test rely on App `InternalsVisibleTo` where already configured — follow existing patterns.
5. Unit tests do **not** replace real UI or scan verification; UI smoke does not replace live-game OCR/DPI validation.
6. Prefer realistic coverage of parsers, projections, presentation helpers, display config, and reliability guards over excessive mocking of the full scan path.
7. Assertions must make the regression clear (expected ids, keys, TTLs, status codes), not only “no exception”.
8. Root authority rules apply: explicit maintainer architecture and repository-ownership decisions outrank transitional implementation or generated guidance. If sources conflict, stop and surface the conflict instead of choosing silently; update this file when test policy changes.
9. RatEye engine internals, OpenCV processing, cache behavior, and replay benchmarks are tested in the RatEye submodule. Keep this project focused on App-owned capture geometry, integration, and presentation contracts.
10. UI tests must refuse to attach to or stop an existing RatScanner instance, avoid fixed sleeps, use semantic locators where the control is interactable, capture actionable failure artifacts, and clean up their own app/profile.

## Prefer

- Extend the adjacent test class when one exists (display/config migration, localization, tarkov.dev/cache/presentation, tracker/update reliability, OpenCV pipeline).
- Small focused facts with clear arrange/act/assert names matching current style.
- When adding App APIs that are pure, add regression tests in the same PR.
- Name files and types after the unit under test.
- Keep `ScanPipelineImageHarnessTests` here because it mirrors RatScanner capture/crop behavior; use RatEye replay manifests for engine-only fixture assertions.

## Validate

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify.ps1 -Mode Fast
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify.ps1 -Mode Ui
```

CI runs Release test configuration — keep tests configuration-agnostic.
