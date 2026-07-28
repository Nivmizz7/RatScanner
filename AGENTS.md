# RatScanner — Agent Control Plane

**Authority:** explicit maintainer decisions are authoritative for architecture and repository ownership, even when the current checkout or generated guidance still reflects an older or transitional layout. Implementation and project files are authoritative for current behavior. If those sources conflict, stop and surface the conflict instead of choosing one silently. Never close or supersede architecture PRs, delete source-bearing work, or reverse a repository boundary based only on inferred documentation precedence. Keep this file and `docs/agent-context/` synchronized when architecture, commands, packages, workflows, or behavior change.

## Product (one paragraph)

Windows x64-only Escape from Tarkov external item scanner. WPF hosts MudBlazor UI via WebView2; screenshots feed standalone RatEye source checked out as a Git submodule under `src/ScanEngine/`. Catalog data comes mainly from **json.tarkov.dev**; maps use a slim GraphQL query on **api.tarkov.dev** with JSON fallback. Maintained at `tarkovtracker-org/RatScanner`, semver **4.x** (`v4.x.x`).

**Stack snapshot:** `net10.0-windows10.0.22621.0` · WPF + WinForms · Blazor WebView · MudBlazor · RatStash · OpenCvSharp · Tesseract · Newtonsoft.Json. Package versions live in `.csproj` files — do not copy versions into docs.

## Non-negotiable constraints

1. **Windows x64 only** — the OpenCvSharp native runtime is x64-only. Do not design, build, test, or document x86, Linux/WSL, or macOS runs.
2. **Scan engine is standalone** — `src/ScanEngine/` is the `tarkovtracker-org/RatEye` submodule. Engine changes belong in RatEye and RatEye must never reference RatScanner. Never add a NuGet `PackageReference` for `RatEye`; App uses a source `ProjectReference`. A temporary vendored or in-tree checkout during migration is not authority to collapse the repositories or abandon the standalone boundary.
3. **Bulk catalog via json.tarkov.dev** — use `TarkovDevAPI` (rate limit, dedup, offline cache, backoff). Do not bypass with ad-hoc HTTP for items/tasks/hideout/crafts/barters. Do not reintroduce a GraphQL schema generator for bulk catalog. Slim maps GraphQL is intentional; keep maps off cold-start critical path.
4. **Product version only in** `src/App/RatScanner.csproj` `<Version>`. Independent 4.x line; do not mirror historical upstream 3.x tags.
5. **No secrets in git** — tokens live in user `config.cfg` (DPAPI-protected fields where used).
6. **Do not perform dependency upgrades** unless the task explicitly asks for them.
7. **Code is source of truth** — project files, scripts, and behavior beat stale prose.

## Default commands

```bat
dev.bat                          :: preferred local loop (debounced watch: 15s quiet period before rebuild)
dev.bat -NoDebounce              :: original dotnet watch (rebuild on every save)
dev.bat -Debounce N              :: set quiet period to N seconds (default 15)
dev.bat -Once                    :: build + run once
dev.bat -ForceSetup              :: re-download icons/OCR into src\App\Data\
dotnet restore RatScanner.sln
dotnet build RatScanner.sln
dotnet build -c Release RatScanner.sln
dotnet test RatScanner.sln
dotnet tool restore
dotnet csharpier check .
dotnet csharpier format .
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-agent-docs.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\lint-markdown.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\lint-markdown.ps1 -Fix
publish.bat                      :: release package only (not day-to-day)
```

Initialize source dependencies after cloning:

```bat
git submodule update --init --recursive
```

Day-to-day coding uses `dev.bat` / `scripts\dev.ps1` (debounced **restart-on-save** by default — 15s quiet period prevents endless rebuilds during rapid agent edits; `-NoDebounce` restores instant `dotnet watch`). Not full WPF hot reload. CI: `.github/workflows/build.yml` (Windows, .NET 10, documentation and formatting checks, Release build/test, dependency audit, validated single-file package) plus `.github/workflows/release.yml` (manual promotion of the exact successful `master` build artifact to either the opt-in `testing` pre-release channel or broad `latest` channel). Build CI runs on PRs and `master` pushes; releases are manual only.

## Fork / remotes / branches / PRs

| Remote | Repo |
| --- | --- |
| `origin` | `tarkovtracker-org/RatScanner` (push here) |
| `upstream` | `RatScanner/RatScanner` (sync only, rare) |

- Primary integration branch: **`master`** (not `main`).
- Day-to-day work: short-lived `feat/…` / `fix/…` branches, open PRs against `master` on the fork.
- Do not treat classical long-lived git-flow (`develop` as primary) as required for this fork.
- Bare `#NNN` resolves on the fork (few/no issues). Prefer full upstream URLs when needed.
- See `docs/agent-context/contribution-workflow.md` and root `CONTRIBUTING.md`.

## Package management (universal)

- Prefer `dotnet add package` / project references over hand-editing package versions when adding deps.
- Prefer package versions published at least ~7 days; avoid floating `*` / unbounded ranges.
- Confirm a package is already used (or intentionally new) before adding it.
- Keep shared dependency choices compatible across App and ScanEngine; align RatStash major.
- `.csproj` / restore graph are authoritative for installed versions.

## Quality and validation (universal)

Before calling material work done:

| Change class | Minimum checks |
| --- | --- |
| Any code | `dotnet build RatScanner.sln` |
| Behavior covered by tests | `dotnet test RatScanner.sln` |
| C# style-sensitive edits | `dotnet tool restore` + `dotnet csharpier check .` (or format) |
| Any `*.md` edit | `scripts\lint-markdown.ps1 -Fix` then check (tables, fence languages, trailing whitespace) |
| Agent docs / structure | `scripts\check-agent-docs.ps1` |
| UI / Razor / CSS | manual WebView smoke via `dev.bat` when practical |
| Scan / OCR | unit tests if present + manual scan smoke when practical |
| i18n keys / UI strings | update every `src/App/i18n/*.json` (en is baseline) |
| Publish / release | `publish.bat` or CI-equivalent; verify LICENSE + Data layout |

Nullable reference types are enabled in App. Implicit usings are **disabled** in App — keep explicit `using` directives. Prefer cascade/component APIs over CSS `!important` (see App UI context).

## Proactive issue ownership (universal)

Investigate, fix, and validate meaningful defects discovered during the task, including pre-existing problems. Trace beyond changed lines when needed for the complete fix. Add focused regression coverage and keep materially separate fixes reviewable when practical.

Do not expand into subjective rewrites or unrelated features. Defer only for a concrete blocker such as an unresolved product decision, unavailable credentials/hardware, destructive action requiring approval, insufficient evidence for a safe fix, or an upstream defect without a safe local workaround. Report the evidence, impact, investigation, blocker, required next action, and release impact for anything left unresolved.

## Documentation maintenance (universal)

- After changing architecture, commands, packages, CI, paths, or product behavior: update **this file** if a control-plane fact moved, and the **relevant** `docs/agent-context/*.md` and nested `AGENTS.md`.
- Do not paste version numbers, exhaustive file lists that will rot, or aspirational roadmaps into agent docs.
- Nested `AGENTS.md` hold path-scoped mandatory rules; context docs hold explanation.
- Run `scripts\check-agent-docs.ps1` after structural doc or packaging-reference changes.
- **Markdown style (mandatory after any `*.md` change):** run `scripts\lint-markdown.ps1 -Fix` (or `npm run lint:md:fix`). Config: `.markdownlint.json` + `.markdownlint-cli2.jsonc`. Prefer compact GFM tables (`| a | b |` with delimiter `| --- | --- |` — spaces around pipes). Fenced blocks need a language (` ```bat `, ` ```text `). Line length is **not** enforced. Optional local git hook: `scripts\install-git-hooks.ps1`.

## Context routing (read before material changes)

| Work area | Required context |
| --- | --- |
| Startup, WPF host, WebView2, DI, lifecycle | [architecture.md](docs/agent-context/architecture.md), [app-ui.md](docs/agent-context/app-ui.md) |
| Razor, MudBlazor, CSS, themes, layout | [app-ui.md](docs/agent-context/app-ui.md) + `src/App/AGENTS.md` |
| Screenshot, OCR, image processing, detection | [scan-engine.md](docs/agent-context/scan-engine.md) + `src/ScanEngine/AGENTS.md` |
| tarkov.dev, maps, JSON/GraphQL, cache, locale data | [data-integrations.md](docs/agent-context/data-integrations.md) |
| Config, settings file, cache paths | [configuration-and-cache.md](docs/agent-context/configuration-and-cache.md) |
| UI string localization | [localization.md](docs/agent-context/localization.md) |
| Build, tests, fixtures, visual verification | [build-and-validation.md](docs/agent-context/build-and-validation.md) + `tests/AGENTS.md` |
| Package / framework upgrades | [dependency-management.md](docs/agent-context/dependency-management.md) |
| Versioning, publishing, releases | [release-and-versioning.md](docs/agent-context/release-and-versioning.md) |
| Branches, commits, PRs, upstream | [contribution-workflow.md](docs/agent-context/contribution-workflow.md) |
| Unfamiliar repo areas | [repository-map.md](docs/agent-context/repository-map.md), [architecture.md](docs/agent-context/architecture.md) |
| Local setup / day-to-day loop | [local-development.md](docs/agent-context/local-development.md) |
| Product purpose / non-goals | [project-overview.md](docs/agent-context/project-overview.md) |

Index and maintenance rules for the context set: [docs/agent-context/README.md](docs/agent-context/README.md).

## Nested instruction files

| Path | Scope |
| --- | --- |
| `src/App/AGENTS.md` | App UI, hosting, data clients owned by App |
| `src/ScanEngine/AGENTS.md` | Standalone RatEye submodule |
| `tests/AGENTS.md` | Unit tests |

## Source-of-truth reminder

**Explicit maintainer architecture decisions win over stale or generated guidance. Code, `.csproj`, scripts, and CI define current behavior but may represent a transitional migration state.** If sources disagree, preserve active work, report the conflict, and obtain direction before changing repository ownership or PR state. Fix confirmed documentation drift in the same change set when you touch the related system.
