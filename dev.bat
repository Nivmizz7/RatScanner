@echo off
setlocal
:: Stupid-proof local dev entrypoint.
:: Default: restore (if needed via script), ensure Data/, then dotnet watch run.
::
:: Usage:
::   dev.bat              watch mode (auto rebuild + restart on save)
::   dev.bat -Once        build and run once
::   dev.bat -ForceSetup  re-download icons/OCR data
::   dev.bat -Release     Release configuration
::   dev.bat -SkipRestore skip NuGet restore

cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\dev.ps1" %*
set EXITCODE=%ERRORLEVEL%
if %EXITCODE% neq 0 (
	echo.
	echo dev.bat failed with exit code %EXITCODE%.
	exit /b %EXITCODE%
)
endlocal
