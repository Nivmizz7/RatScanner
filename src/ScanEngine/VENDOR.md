# Vendored scan engine (historical RatEye)

Folder: `src/ScanEngine/`
Assembly / namespaces: still **`RatEye`** (unchanged for now to avoid a large API rename).

Sources vendored from <https://github.com/RatScanner/RatEye> at tag `v4.0.1`
(`862b78002a59286aaed6d4d94dce87ce9a989462`).

This project is referenced by `src/App` via `ProjectReference` so image-processing
changes land in the same PR as the app. Do not re-add the RatEye NuGet package.

Test assets from upstream `RatEyeTest` are intentionally not included (large binaries).

## License note

Upstream `RatScanner/RatEye` did **not** ship a `LICENSE` file at the time of vendoring
(GitHub license metadata was empty). This product's root `LICENSE` (Elastic License 2.0–based)
applies to **this repository as distributed by this fork**, but does **not** automatically
rewrite upstream copyright. Keep provenance here until the original author confirms terms.
