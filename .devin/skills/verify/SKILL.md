---
name: verify
summary: Drive the WPF-hosted Blazor UI through WebView2 CDP.
description: Launch and runtime-verify RatScanner UI changes with an isolated WebView2 profile and Chrome DevTools Protocol.
---

# Verify RatScanner UI

1. Establish scope with `git diff HEAD --stat`.
2. Launch an isolated instance from PowerShell:

   ```powershell
   $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS='--remote-debugging-port=9227'
   $env:WEBVIEW2_USER_DATA_FOLDER="$env:TEMP\RatScanner-verify"
   & '.\scripts\dev.ps1' -Once
   ```

3. Query `http://127.0.0.1:9227/json/version`, connect to its `webSocketDebuggerUrl`, call `Target.getTargets`, and attach with `flatten: true` to the page target at `https://0.0.0.1/app`.
4. Use `Runtime.evaluate` to inspect and operate the real rendered DOM. Use `Page.captureScreenshot` for evidence.
5. Drive the changed user flow plus at least one adjacent failure/state probe. For Recent scans, select real catalog items through `input[placeholder="Search items"]`; verify navigation persistence, five-item eviction, and duplicate promotion.
6. Stop only the RatScanner process launched for verification.

## Gotchas

- `/json/list` can be empty even when CDP works. Use the browser websocket and `Target.getTargets`.
- Existing WebView2 processes may ignore the debugging port. Always set a unique `WEBVIEW2_USER_DATA_FOLDER`.
- The WPF accessibility tree exposes native chrome but not Blazor content; CDP is the reliable interaction surface.
- Do not treat build/tests as runtime evidence; report them separately if the implementation workflow requires them.
