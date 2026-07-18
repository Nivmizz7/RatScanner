# Release and versioning

## Product versioning

| | Historical upstream | This project |
| --- | --- | --- |
| Line | 3.x | **4.x** starting at 4.0.0 |
| Tag form | `v3.x.x` | `v4.x.x` (pre-release suffixes supported, e.g. `v4.0.0-beta.1`) |
| UI token | — | `v4.x.x` (`MenuVM.VersionDisplay`) |
| Log label | — | `RatConfig.FullVersionLabel` / `Constants.Branding` |

**Bump only** `<Version>` in `src/App/RatScanner.csproj`. Do not mirror historical upstream patch numbers.

ScanEngine's own `<Version>` is historical engine packaging metadata; product releases are App-driven.

### Semver guidance

| Bump | When |
| --- | --- |
| Major | Breaking change for end users |
| Minor | Feature / significant behavior change |
| Patch | Bug fix or config-only user impact |
| Pre-release suffix | Beta/RC phases (e.g. `-beta.1`, `-rc.1`) |
| None | Documentation-only |

### Prevention of accidental upstream reuse

- Do not tag or ship `3.x` on this project.
- Issues that cite only `3.9.x` are almost certainly not this build.

## Local publish

```bat
publish.bat
```

Matches the CI packaging intent:

- `dotnet publish src/App/RatScanner.csproj -c Release -o publish --runtime win-x64 -p:PublishSingleFile=true --self-contained true`
- Ensure `LICENSE` in output
- Download and extract latest RatScannerData into `publish\Data`
- Zip to `RatScanner.zip`

Day-to-day coding should use `dev.bat`, not publish.

## CI release path

Workflow: `.github/workflows/build.yml`

Triggers:

- PRs to `master`
- Pushes to `master`
- Push tags `v*`
- `workflow_dispatch`

The build job (Windows, .NET 10.x, `contents: read`) runs agent-docs regression/integrity checks → Markdown and C# formatting → restore/build/test Release → .NET/npm vulnerability audits → publish single-file → validate LICENSE + Data → upload `publish/` and `RatScanner.zip`.

A separate tag-only release job has `contents: write`. It downloads the validated artifact, rejects a tag that is not exactly `v` + the App project `<Version>`, and creates the **draft** GitHub release. PR and branch-push builds never receive release permissions.

`softprops/action-gh-release` must stay pinned to a commit SHA on the TarkovTracker org **selected actions** allowlist. GitHub rejects the entire workflow at startup (including the non-release build job) if the SHA is not listed. Process for bumps: (1) review the new softprops release, (2) add the SHA via org Actions selected-actions policy (`admin:org`), (3) update this workflow pin, (4) only then remove older softprops SHAs from the allowlist once no org workflow still references them.

## Update channel in-app

`GitHubUpdateService` checks `tarkovtracker-org/RatScanner` releases and applies zip swap. It does **not** use upstream `api.ratscanner.com` updater endpoints.

## Checklist before tagging

1. Version bumped in App csproj; the release tag must match it exactly with a leading `v`.
2. `dotnet test` / build green.
3. `scripts\check-agent-docs.ps1` green if docs/packaging references changed.
4. Publish smoke or trust CI.
5. Changelog/notes ready for draft release body if needed.
6. Tag `vMAJOR.MINOR.PATCH[-prerelease]` on the intended commit; push to **origin**.
7. Promote draft release when verified.

## Branding

Product name is `RatScanner` (`Constants.Branding.Name`). User-agent strings use `RatScanner/<version>`. Keep modification notices (README attribution, Credits/About) intact for license compliance.
