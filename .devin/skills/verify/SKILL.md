---
name: verify
summary: Drive the WPF-hosted Blazor UI through WebView2 CDP.
description: Launch and runtime-verify RatScanner UI changes with an isolated WebView2 profile and Chrome DevTools Protocol.
---

# Verify RatScanner UI

1. Record the exact scope with `git status --short`, the PR diff, and the working-tree diff. Decide which rendered behavior and adjacent failure state must be proved.
2. Ensure no RatScanner instance is already running, then launch the app from a dedicated PowerShell session. Keep that session open so the variables remain available for cleanup:

   ```powershell
   if (Get-Process -Name RatScanner -ErrorAction SilentlyContinue) {
       throw 'Close the existing RatScanner instance; verification must not attach to or stop an unrelated process.'
   }

   $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
   $listener.Start()
   $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
   $listener.Stop()

   $verifyRoot = Join-Path $env:TEMP ("RatScanner-verify-" + [Guid]::NewGuid().ToString('N'))
   $profile = Join-Path $verifyRoot 'WebView2'
   New-Item -ItemType Directory -Path $profile -Force | Out-Null

   $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS="--remote-debugging-port=$port"
   $env:WEBVIEW2_USER_DATA_FOLDER=$profile
   $devScript = (Resolve-Path '.\scripts\dev.ps1').Path
   $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$devScript`" -Once"
   try {
       $launcher = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -PassThru `
           -RedirectStandardOutput (Join-Path $verifyRoot 'stdout.log') `
           -RedirectStandardError (Join-Path $verifyRoot 'stderr.log')
   }
   finally {
       Remove-Item Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -ErrorAction SilentlyContinue
       Remove-Item Env:WEBVIEW2_USER_DATA_FOLDER -ErrorAction SilentlyContinue
   }
   ```

3. Poll `http://127.0.0.1:$port/json/version` until it returns `webSocketDebuggerUrl`. Connect to that browser websocket, call `Target.getTargets`, and attach with `flatten: true` to the `page` target whose URL pathname is `/app`. Select by pathname instead of hard-coding the private host origin (the current .NET 10 WebView package uses `https://0.0.0.1/app`).
4. Use `Runtime.evaluate` to inspect and operate the real rendered DOM. Use `Page.captureScreenshot` for visual evidence and inspect the resulting image.
5. Drive the changed flow plus at least one adjacent failure or state transition. For Recent scans, use the locale-independent `.rs-search-field input` selector to choose real catalog items; verify navigation persistence, five-item eviction, and duplicate promotion.
6. Always clean up only the process tree and profile created above, including after a failed probe:

   ```powershell
   if ($launcher) {
       & taskkill.exe /PID $launcher.Id /T /F 2>$null | Out-Null
   }
   Start-Sleep -Milliseconds 500
   Remove-Item -LiteralPath $verifyRoot -Recurse -Force -ErrorAction SilentlyContinue
   ```

7. Report build/test results separately from runtime evidence. Name each screenshot or DOM probe and the behavior it proves.

## Gotchas

- `/json/list` can be empty even when CDP works. Use the browser websocket from `/json/version` and `Target.getTargets`.
- RatScanner is single-instance. Never stop an existing user process to make the verification launch succeed.
- A unique user-data folder prevents an existing WebView2 process from absorbing the debugging arguments or contaminating state.
- The WPF accessibility tree exposes native chrome but not Blazor content; CDP is the reliable interaction surface.
- Do not treat build or unit-test success as runtime UI evidence.
