# Tests (`tests/`) — scoped agent instructions

Read with root `AGENTS.md` and `docs/agent-context/build-and-validation.md` before adding or changing tests.

## Scope

xUnit unit tests under `tests/RatScanner.Tests` that reference the App project. Windows x64 TFM (same family as App/native runtime).

## Mandatory

1. Tests must run with `dotnet test RatScanner.sln` on Windows.
2. Prefer **pure, hermetic, deterministic** tests (no live network, no game client, no real WebView, no real clipboard/hotkeys).
3. Do not commit secrets, large binary fixtures, or full Data icon dumps. If a screenshot/OCR fixture is essential, keep it small, document provenance in the test, and avoid copyright-sensitive game assets when possible.
4. Internals under test rely on App `InternalsVisibleTo` where already configured — follow existing patterns.
5. Unit tests do **not** replace manual UI or scan verification; do not claim they do.
6. Prefer realistic coverage of parsers, projections, presentation helpers, display config, and reliability guards over excessive mocking of the full scan path.
7. Assertions must make the regression clear (expected ids, keys, TTLs, status codes), not only “no exception”.
8. Root authority rules apply: explicit maintainer architecture and repository-ownership decisions outrank transitional implementation or generated guidance. If sources conflict, stop and surface the conflict instead of choosing silently; update this file when test policy changes.
9. RatEye engine internals, OpenCV processing, cache behavior, and replay benchmarks are tested in the RatEye submodule. Keep this project focused on App-owned capture geometry, integration, and presentation contracts.

## Prefer

- Extend the adjacent test class when one exists (display/config migration, localization, tarkov.dev/cache/presentation, tracker/update reliability, OpenCV pipeline).
- Small focused facts with clear arrange/act/assert names matching current style.
- When adding App APIs that are pure, add regression tests in the same PR.
- Name files and types after the unit under test.
- Keep `ScanPipelineImageHarnessTests` here because it mirrors RatScanner capture/crop behavior; use RatEye replay manifests for engine-only fixture assertions.

## Validate

```bat
dotnet test RatScanner.sln
```

CI runs Release test configuration — keep tests configuration-agnostic.
