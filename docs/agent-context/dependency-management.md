# Dependency management

## Authority

**Project files are the source of truth** for package versions and project references:

- `src/App/RatScanner.csproj`
- `src/ScanEngine/RatEye/RatEye.csproj` (RatEye submodule)
- `tests/RatScanner.Tests/RatScanner.Tests.csproj`
- `tests/RatScanner.UiTests/RatScanner.UiTests.csproj`
- `NuGet.Config` / nuget.org
- `Directory.Build.targets` (disables RatEye package generation in RatScanner builds)
- `dotnet-tools.json` (CSharpier)

Do not document “current version is X” in prose. Read the csproj.

## Project ownership of packages

| Concern | Typical home |
| --- | --- |
| WPF Blazor WebView, MudBlazor, SingleInstanceCore, Windows Compatibility | App |
| OpenCvSharp, engine Tesseract.Drawing, System.Drawing.Common | ScanEngine |
| RatStash, Tesseract, Newtonsoft.Json | Shared across App/ScanEngine as needed |
| xUnit, test SDK | Tests only |
| Playwright .NET CDP client | UI tests only; connects to installed WebView2, no bundled browser install |

RatEye remains independently packable from its own repository. RatScanner does not publish or consume that package during development.

## Hard constraints

1. **No NuGet RatEye** — App uses `<ProjectReference Include="..\ScanEngine\RatEye\RatEye.csproj" />`.
2. Keep **RatStash major** aligned between App and ScanEngine so one assembly loads.
3. Prefer existing libraries already in the graph over new packages.
4. Prefer versions published ≥ ~7 days; avoid floating `*` / open-ended ranges / `latest`.
5. Prefer `dotnet add package` when adding packages.
6. Do not “fix” CI by disabling security/minimum age policies if introduced later — escalate.
7. Align native runtimes with managed packages (OpenCvSharp.Windows version with Extensions; Tesseract with traineddata expectations).

## Legitimate reasons for retaining a package version

- Breaking API surface that would force large product risk without a dedicated migration.
- Known good OCR/runtime pairing with the traineddata under `Data/` until OCR accuracy is revalidated with representative captures.
- Framework alignment constraints that remain intentional after review.
- Transitive skew that restores cleanly and is not a security gate.

Do not maintain a duplicated version inventory here. Read the project files and resolved assets, then record any retained-version rationale in the change that reviews it. OpenCvSharp managed + native packages must remain **paired**. Prefer the full Windows runtime unless every used module is confirmed present.

## Upgrade discipline

Unless the user explicitly requests dependency upgrades:

- Do not bump packages “while you’re here.”
- Do not modernize framework TFMs without a dedicated task.
- Do not unify Newtonsoft versions just for neatness if restore already works (minor skew exists historically).
- Do not perform blind major upgrades (MudBlazor, WebView, OpenCV) without migration guides and validation plan.

When upgrades **are** requested:

1. Read this file + affected csproj + official release notes / migration guides.
2. One logical upgrade set per PR when possible.
3. `dotnet restore` + `dotnet build` + `dotnet test`.
4. Manual smoke for UI or native packages (OpenCvSharp, WebView, Tesseract).
5. Update agent context only if ownership/process changes — not version numbers.

## Framework alignment

- App: `net10.0-windows10.0.22621.0` with `EnableWindowsTargeting`.
- RatEye library: `netstandard2.0`; confirm language settings in the submodule project.
- App/tests: x64-only in all configurations to match the OpenCvSharp Windows native runtime; do not restore x86 solution configurations.
- Tests: same Windows TFM family as App.
- CI installs .NET `10.0.x`.

## Vulnerability / outdated inspection

```bat
dotnet list RatScanner.sln package --vulnerable
dotnet list RatScanner.sln package --outdated
dotnet list tests\RatScanner.UiTests\RatScanner.UiTests.csproj package --vulnerable --include-transitive
dotnet list tests\RatScanner.UiTests\RatScanner.UiTests.csproj package --outdated
```

Triage with product risk (native, WebView, JSON, auth). Prefer minimal fixing upgrades.

## Tools

- CSharpier version pinned in `dotnet-tools.json`.
- Restore tools: `dotnet tool restore`.
- Invoke: `dotnet csharpier check .` / `dotnet csharpier format .`.
