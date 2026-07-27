# Release and versioning

## Product versioning

| | Historical upstream | This project |
| --- | --- | --- |
| Line | 3.x | **4.x** starting at 4.0.0 |
| Tag form | `v3.x.x` | `v4.x.x` (pre-release suffixes supported, e.g. `v4.0.0-beta.1`) |
| UI token | — | `v4.x.x` (`MenuVM.VersionDisplay`) |
| Log label | — | `RatConfig.FullVersionLabel` / `Constants.Branding` |

**Bump only** `<Version>` in `src/App/RatScanner.csproj`. Do not mirror historical upstream patch numbers.

RatEye owns its own package version in the submodule. RatScanner product releases remain App-driven and do not reuse RatEye's version.

### Semver guidance

| Bump | When |
| --- | --- |
| Major | Breaking change for end users |
| Minor | Feature / significant behavior change |
| Patch | Bug fix or config-only user impact |
| Pre-release suffix | Iterations toward one target version (e.g. `-beta.1`, `-rc.1`) |
| None | Documentation-only |

### Pre-release lifecycle

Pre-release suffixes describe maturity **toward the stable numeric version before the suffix**. For example, every `4.0.1-*` build is a candidate for the eventual stable `4.0.1` release.

| Stage | Meaning | Example |
| --- | --- | --- |
| Alpha | Experimental or incomplete; core design/features may still change substantially | `4.0.1-alpha.1` |
| Beta | Usable early test build; core behavior exists, but defects and rough areas are expected | `4.0.1-beta.1` |
| RC | Release candidate; believed ready for stable release unless a significant defect is found | `4.0.1-rc.1` |
| Stable | Supported release with the pre-release suffix removed | `4.0.1` |

Number iterations within a stage instead of consuming patch versions:

```text
4.0.1-alpha.1
4.0.1-beta.1
4.0.1-beta.2
4.0.1-rc.1
4.0.1
```

SemVer precedence determines update order: `alpha.1 < beta.1 < beta.2 < rc.1 < stable`. Build metadata such as `+build.5` does not affect precedence and must not be used to sequence releases. Never replace or modify an existing release; every published build gets a unique version and tag.

### Prevention of accidental upstream reuse

- Do not tag or ship `3.x` on this project.
- Issues that cite only `3.9.x` are almost certainly not from the 4.x line.

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

Workflows:

- `.github/workflows/build.yml` — PRs to `master` and pushes to `master`.
- `.github/workflows/release.yml` — manual artifact promotion from `master` only.

The build job (Windows, .NET 10.x, `contents: read`) runs agent-docs regression/integrity checks → Markdown and C# formatting → restore/build/test Release → .NET/npm vulnerability audits → publish single-file → validate LICENSE + Data → upload `RatScanner.zip` as an immutable workflow artifact.

The release workflow does **not** rebuild. It resolves `<Version>` from the selected `master` commit, requires a successful push-triggered Build run for that exact commit, and downloads that run's `RatScanner.zip` with GitHub's artifact digest verification enabled. It then validates the zip, refuses to overwrite an existing tag or draft/published release, and auto-creates the tag. Only the Release workflow receives `actions: read` and `contents: write`; PR and branch-push builds remain read-only.

The manual workflow requires an explicit publishing channel:

| Channel | GitHub release state | Who receives it automatically |
| --- | --- | --- |
| `testing` | GitHub pre-release; not returned by `/releases/latest` | Users running a pre-release build with the prerelease-aware updater, including users moved onto it by a bridge release |
| `latest` | Non-pre-release and marked Latest | All eligible installed builds, including the currently published `4.0.0-beta` bridge population |

Use `testing` for smoke testing and normal numbered beta/RC iterations. Use `latest` only when intentionally promoting a build to the entire maintained update feed. The in-app updater keeps stable installs on `/releases/latest`; pre-release installs query published releases and follow newer SemVer pre-releases before moving to the matching stable release.

### How to cut a release

Preferred (one click, no local git or duplicate build):

1. Bump `<Version>` in `src/App/RatScanner.csproj` and land it on `master`.
2. Wait for the push-triggered **Build** workflow on that `master` commit to pass.
3. GitHub → **Actions** → **Release** → **Run workflow** from `master`.
4. Select `testing` for an opt-in pre-release or `latest` for deliberate broad rollout.
5. CI promotes the successful Build artifact and tags `v<Version>` without rebuilding.

Safe bridge rollout for the updater change:

1. Publish `4.0.1-beta.1` as `testing`; this does **not** replace the current Latest release and does not notify `4.0.0-beta` installs.
2. Download and run `4.0.1-beta.1` manually in a disposable copy of the install directory; verify startup and automatic updating with a later testing build.
3. After smoke testing, publish the next unused pre-release iteration on the same numeric target (for example `4.0.1-beta.3` if `beta.2` was the later testing build) as `latest` to move the existing `4.0.0-beta` population onto the prerelease-aware updater. Never reuse or overwrite a testing tag.

`GitHubUpdateService` compares full SemVer precedence, including numbered alpha/beta/RC identifiers, while ignoring build metadata. A stable installation will not automatically move onto a pre-release channel. A pre-release installation follows newer pre-releases and then the matching stable release.

`softprops/action-gh-release` must stay pinned to a commit SHA on the TarkovTracker org **selected actions** allowlist. GitHub rejects the entire workflow at startup (including the non-release build job) if the SHA is not listed. Process for bumps: (1) review the new softprops release, (2) add the SHA via org Actions selected-actions policy (`admin:org`), (3) update this workflow pin, (4) only then remove older softprops SHAs from the allowlist once no org workflow still references them.

## Update channel in-app

`GitHubUpdateService` checks `tarkovtracker-org/RatScanner` releases and applies zip swap. It does **not** use upstream `api.ratscanner.com` updater endpoints. Stable installations read `/releases/latest`, while pre-release installations query the published releases list so they can receive newer testing builds. Updates are offered only when the strict SemVer precedence is higher; stable installations never move onto a pre-release channel.

## Checklist before tagging

1. Version bumped in App csproj; the release tag must match it exactly with a leading `v`. Reuse the same numeric target while iterating pre-releases (`4.0.1-beta.1` → `4.0.1-beta.2` → `4.0.1-rc.1` → `4.0.1`).
2. `dotnet test` / build green.
3. `scripts\check-agent-docs.ps1` green if docs/packaging references changed.
4. Publish smoke or trust CI.
5. Changelog/notes ready for draft release body if needed.
6. Deploy: **Actions → Release → Run workflow** from `master`, choosing `testing` or `latest` deliberately.
7. Confirm `testing` releases are marked **Pre-release**, or confirm broad-rollout releases are marked **Latest**, and verify `RatScanner.zip` is attached.

## Branding

Product name is `RatScanner` (`Constants.Branding.Name`). User-agent strings use `RatScanner/<version>`. Keep modification notices (README attribution, Credits/About) intact for license compliance.
