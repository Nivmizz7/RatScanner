# Contributing

## Where to contribute

This project is maintained at [tarkovtracker-org/RatScanner](https://github.com/tarkovtracker-org/RatScanner).

Open PRs and issues on **this** repository. Historical upstream [RatScanner/RatScanner](https://github.com/RatScanner/RatScanner) is inactive for day-to-day work.

## Branch workflow

Supported workflow for this fork:

1. Branch from **`master`** (`feat/…`, `fix/…`).
2. Keep changes focused; open a PR against `master` on the fork.
3. CI (`.github/workflows/build.yml`) runs on Windows with .NET 10, documentation/formatting checks, Release build/tests, dependency audit, and publish packaging. Releases promote the exact successful `master` build artifact through `.github/workflows/release.yml` without rebuilding.

`master` is the primary integration branch. Classical long-lived git-flow with `develop` as the main integration target is **not** required for day-to-day work on this fork.

### Remotes (developers and agents)

| Remote | Purpose |
| --- | --- |
| `origin` | `tarkovtracker-org/RatScanner` (push) |
| `upstream` | `RatScanner/RatScanner` (optional sync only) |

## Development

Requirements: **64-bit Windows**, [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bat
dev.bat                 :: watch rebuild + restart (preferred)
dev.bat -Once           :: run once
dotnet restore RatScanner.sln
dotnet build RatScanner.sln
dotnet test RatScanner.sln
dotnet tool restore
dotnet csharpier check .
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-agent-docs.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\lint-markdown.ps1 -Fix
```

Markdown style is enforced by **markdownlint-cli2** (Node). After editing any `*.md`, run the lint script with `-Fix` (or `npm run lint:md:fix`). CI runs the same lint in check mode. Optional local pre-commit: `scripts\install-git-hooks.ps1`.

Do **not** use `publish.bat` for everyday coding. Details: `docs/agent-context/local-development.md` and root `README.md`.

Agent and architecture guidance: root `AGENTS.md` + `docs/agent-context/`.

## Code expectations

- Match surrounding style; nullable reference types are enabled in the App; implicit usings are disabled (keep explicit usings).
- Prefer clear structure over commentary that restates the code.
- MudBlazor/CSS: prefer component parameters and specificity over `!important`.
- Bulk catalog data goes through `TarkovDevAPI` (json.tarkov.dev); maps may use intentional slim GraphQL + JSON fallback. Do not reintroduce GraphQL schema generation for bulk catalog or a NuGet `RatEye` package.
- Edit the in-repo scan engine under `src/ScanEngine/` (namespaces remain `RatEye`).

## Documentation

If your change alters architecture, commands, packages, CI, config paths, or product behavior, update the matching agent context under `docs/agent-context/` and any nested `AGENTS.md`. Implementation and project files always override stale docs. Run `scripts\check-agent-docs.ps1` after structural documentation changes.

## Versioning

This project uses its **own** [semver](https://semver.org/) line so releases and bug reports are never confused with historical upstream RatScanner.

| | Historical upstream | This project |
| --- | --- | --- |
| Product | RatScanner | RatScanner |
| Major line | `3.x` (e.g. `3.9.3`) | **`4.x`** (starts at `4.0.0`) |
| UI / logs | `v3.9.3` | `v4.0.0` / full label in logs |
| Tags | `v3.9.3` | `v4.0.0` (pre-release suffixes supported, e.g. `v4.0.0-beta.1`) |

Do **not** reuse or "continue" historical upstream patch numbers. After a breaking change, bump major; otherwise minor/patch as usual.

**Where to bump:** only `<Version>` in `src/App/RatScanner.csproj`.

```xml
<Version>4.0.0-beta.1</Version>
```

**Release tags:** `vMAJOR.MINOR.PATCH[-prerelease]` (e.g. `v4.0.1`, `v4.0.0-beta.2`). After the version lands on `master` and its Build passes, the manual Release workflow promotes that exact CI artifact, creates the tag, and publishes it as Latest.

| Bump | When |
| --- | --- |
| **Major** | Breaking change for end users of this fork |
| **Minor** | New feature / significant behavior change |
| **Patch** | Bug fix or config-only change |
| *(none)* | Documentation-only |

Version format:

```text
Major.Minor.Patch
```

More detail: `docs/agent-context/release-and-versioning.md`.
