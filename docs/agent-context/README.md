# Agent context index

## Why this exists

Root `AGENTS.md` is the **control plane**: short universal rules, default commands, and routing. Everything else agents need lives here as **focused context** so agents load only what a task requires.

## Mandatory vs explanatory

| Kind | Where | Role |
| --- | --- | --- |
| **Mandatory instructions** | Root `AGENTS.md`, nested `**/AGENTS.md` | Always or path-scoped rules that must be followed |
| **Explanatory context** | Files in this directory | How systems work, where code lives, what to verify |

Context docs are not a second source of product truth. If prose disagrees with code, scripts, `.csproj`, or CI, **implementation wins** — then update the prose.

## Files and when to read them

| File | Read when |
| --- | --- |
| [project-overview.md](project-overview.md) | Orienting on product purpose, platform, fork identity, non-goals |
| [architecture.md](architecture.md) | Startup, process shape, WPF vs Blazor WebView, DI, lifecycle |
| [repository-map.md](repository-map.md) | Locating concerns and directories before a search binge |
| [local-development.md](local-development.md) | Setup, `dev.bat`, data install, watch loop failures |
| [build-and-validation.md](build-and-validation.md) | Restore/build/test/format, what to run per change type |
| [app-ui.md](app-ui.md) | Razor, MudBlazor, CSS, themes, host pages, layouts |
| [scan-engine.md](scan-engine.md) | OCR/icon pipeline and standalone RatEye boundary |
| [data-integrations.md](data-integrations.md) | json.tarkov.dev, slim maps GraphQL, TarkovTracker, caches |
| [configuration-and-cache.md](configuration-and-cache.md) | `config.cfg`, paths, TTL, offline cache |
| [localization.md](localization.md) | UI `i18n` files and `LocalizationService` |
| [dependency-management.md](dependency-management.md) | Packages, ProjectReference, upgrade discipline |
| [release-and-versioning.md](release-and-versioning.md) | 4.x semver, publish, CI tags, fork branding |
| [contribution-workflow.md](contribution-workflow.md) | Remotes, branches, PRs, docs-with-code |

Root routing table: [`AGENTS.md`](../../AGENTS.md).

## Maintenance rules

1. When you change architecture, commands, packages, CI, paths, or user-visible behavior, update the **matching** context file(s) in the same change.
2. Prefer links to authoritative files (`*.csproj`, scripts, workflows) over restating content that will drift.
3. Do not copy package version numbers into these docs.
4. Nested `src/App/AGENTS.md` and `tests/AGENTS.md` hold RatScanner-scoped rules. The `src/ScanEngine` submodule supplies RatEye's own `AGENTS.md`.
5. Do not re-expand the root control plane into a subsystem dump.
6. After structural doc changes, run `scripts/check-agent-docs.ps1` (also run in CI).
7. After any `*.md` edit, run `scripts/lint-markdown.ps1 -Fix` (markdownlint-cli2; tables/fences/whitespace). CI enforces check mode; the optional hook checks without rewriting staged content.

## Drift policy

Copied implementation details are a liability. Prefer:

- “See `TarkovDevAPI.cs` for endpoint and TTL behavior”
- over long paraphrases of caching algorithms

If an agent finds drift while working, fix the affected docs as part of the task.

Objective structural checks (required paths, local links, MSBuild XML/project/package constraints, generated-output exclusions, and branch-policy invariants) live in `scripts/check-agent-docs.ps1`. `scripts/test-agent-docs.ps1` exercises representative failures in a disposable fixture. The checker deliberately does **not** try to infer general semantic truth from free-form prose.
