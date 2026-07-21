# ScanEngine (`src/ScanEngine`) — scoped agent instructions

Read with root `AGENTS.md`, `VENDOR.md`, and `docs/agent-context/scan-engine.md` before material processing changes.

## Scope

Image processing / OCR / icon matching library used by the App. Folder name is ScanEngine; **assembly and namespaces remain `RatEye`**.

## Mandatory

1. **No NuGet reintroduction** of RatEye. Consumers use ProjectReference only.
2. **Do not pack/publish** this project as a NuGet package from this monorepo (`IsPackable` / `GeneratePackageOnBuild` stay false).
3. Keep the engine **free of** WPF, Blazor, HTTP API clients, and app settings persistence.
4. Treat `Config` as immutable after engine construction (existing API contract).
5. Dispose paths must continue releasing Tesseract engines, markers, and IconManager resources.
6. Align **RatStash major** with the App when changing that dependency.
7. Keep OpenCvSharp managed/native package versions paired (Extensions + Windows).
8. The current Windows native runtime is x64-only; do not claim or add x86 support without a compatible runtime and validation.
9. Preserve provenance notes in `VENDOR.md` when vendoring facts change; do not invent license claims.
10. Implementation overrides this file; update it when engine-scoped rules change.

## Prefer

- Minimal diffs in Processing/Config for accuracy fixes.
- Log through existing engine logger patterns.
- Let App own capture geometry and tarkov.dev item projection.
- Fixture-oriented pure helpers for new regression surface when practical (upstream binary fixtures are not vendored).

## Validate

Build the full solution (App must compile against engine). Run `dotnet test` when App tests cover related contracts. Manual scan smoke for accuracy-sensitive changes. See `docs/agent-context/build-and-validation.md`.
