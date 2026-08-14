# Performance diagnostics

Read when investigating scan latency, overlay GPU/CPU load, startup time, or a
user report of "it feels slow" that you cannot reproduce.

## Why it exists

Scan and startup slowness is environment-dependent: display count, refresh rate,
DPI, virtual-screen size, and WebView2 version all change the outcome. Startup's
`total` ends at the first main-window paint; deferred overlay/catalog/engine work
continues in the same trace only while that snapshot remains open. Reproducing
a user's machine is usually impossible, so the scanner measures itself and puts the
result where the user already looks — the ordinary log.

Collection is **always on**. Only log verbosity is conditional. A stage is one
`Stopwatch` timestamp plus a list insert, so per-scan overhead is negligible; making
it opt-in would mean the data is never present when somebody actually hits the bug.

## Pieces

| Type | Role |
| --- | --- |
| `Diagnostics/PerfTrace.cs` | One operation's timeline: spans, instant marks, notes, engine-timing merge |
| `Diagnostics/PerfTraceStore.cs` | Owns the startup trace, a bounded ring of recent scan traces, and counters/gauges |
| `Diagnostics/PerfEnvironment.cs` | Machine snapshot: displays, refresh rates, DPI, adapters, virtual-screen area, WebView2 version, WPF render tier, memory |
| `Diagnostics/PerfReport.cs` | Serializable report plus text and compact renderings |
| `wwwroot/js/perf.js` | `RatScannerPerf.awaitFrame()` — resolves after the frame containing a change is composited |

`PerfTrace` is deliberately clock-injectable (`Func<double> nowMs`) so tests drive
it deterministically. Production always uses the monotonic `Stopwatch` clock; wall
clock can step backwards and produce negative durations.

## Where the data appears

| Sink | Contents |
| --- | --- |
| `Log.txt` | Full startup timeline, one `perf env` block, and one line per scan |
| `Log.txt` (expanded) | Full per-scan timeline when `LogDebug` is on **or** the scan exceeded its budget |
| `Debug/ScanDiagnostics/<id>/performance.json` | Machine-readable report, written alongside a scan diagnostics export |
| `Debug/ScanDiagnostics/<id>/performance.txt` | Same report as text, including per-stage median/max across recent scans |
| GitHub issue body | Compact report, added automatically by the crash reporter |

The report contains no screenshots, tokens, or item data — only timings, counters,
and machine description.

## Reading a scan line

```text
perf name-scan #12 total=812.4ms | hook.dispatch 2.6 | scan.settle_sleep 50.5 | scan.screenshot 7.7 | scan.inspect 341.5 | overlay.renderer_resume 180.2 | overlay.visible 278.0 | mainui.visible 400.0 item=Army Crackers outcome=ok
```

Stage names are dot-namespaced by owner:

| Prefix | Owner |
| --- | --- |
| `hook.` | Input hook to handler dispatch |
| `scan.` | App-side capture and orchestration |
| `ratEye.` | Engine-internal stages, merged from `RatEye.ProcessingTimings` |
| `engine.` | Scan-engine construction |
| `overlay.` | Tooltip overlay window, renderer resume, render, paint |
| `mainui.` | Main window result mapping, render, paint |
| `startup.` | Application startup phases |

`*.visible` stages measure time to a composited frame (via `awaitFrame`), not just
to a completed Blazor render. `*.blazor_render` is the render-only figure.

## Trace lifetime

A scan's timeline does not end when the scan method returns: the overlay resumes and
paints, and the main window re-renders, afterwards on other threads. So the trace
stays open and is closed by whichever happens first:

1. The main window reports its paint (`CompleteScan`) — the normal path.
2. The scan produced no tooltip, so the scan method closes it directly.
3. The next scan starts, or a report is requested.
4. The store's finalize timer expires.

Downstream reporters pass the sequence number they observed. A late render therefore
cannot be attributed to the following scan — see `PerfTraceStoreTests`.

## Counters and gauges

Counters answer "how often", which is what matters for sustained GPU cost. Notable
ones:

| Name | Meaning |
| --- | --- |
| `overlay.shown` / `overlay.hidden` | Overlay window show/hide cycles |
| `overlay.bootstrap_ms` | Deferred passive-overlay construction time after first paint |
| `overlay.surface_px` | Composited overlay area; the overlay spans the whole virtual screen |
| `webview.resume_from_suspended` | Renderer un-freezes, each one on a tooltip's critical path |
| `webview.suspend_succeeded` / `webview.suspend_failed` | Renderer suspend outcomes |
| `window.fit_resize` | Main-window height writes; each resizes the WebView2 surface (content-fit animation is intentionally capped near 30 Hz) |
| `window.fit_animation_started` | Content-fit animations begun |
| `engine.rebuild_on_scan_path` | Engine rebuilds that happened inside a scan |
| `scan.throttled` | Scans dropped by the cooldown |

## Adding a stage

1. Prefer `using (trace.Measure("owner.stage"))` around the work.
2. For work timed elsewhere, call `PerfTraceStore.RecordScanStage(sequence, name, ms)`
   with the sequence carried on `ItemScan.PerfSequence`.
3. Use `Increment`/`SetGauge` for repeated events rather than adding a stage per event.
4. Keep names stable — they are compared across releases in user reports.

## Validate

```bat
dotnet test RatScanner.sln
```

`PerfTraceTests` and `PerfTraceStoreTests` cover ordering, clamping, correlation,
retention, and report rendering. Timings themselves need a manual run: start via
`dev.bat`, scan an item, then read `Log.txt`.
