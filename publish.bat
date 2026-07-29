@echo off
setlocal

:: Always replace the same local outputs so repeated builds do not accumulate folders/zips
echo Removing old publish folder and zip...
if exist publish rmdir /s /q publish
if exist RatScanner.zip del /f /q RatScanner.zip

:: Publish (matches CI: Release, win-x64, single-file, self-contained)
echo Publishing RatScanner project...
dotnet publish src/App/RatScanner.csproj -c Release -o publish --runtime win-x64 -p:PublishSingleFile=true --self-contained true
if errorlevel 1 (
	echo Publish failed.
	exit /b 1
)

:: Ensure LICENSE is always present for redistributed packages (license notice requirement)
if not exist "publish\LICENSE" (
	if exist "LICENSE" copy /y "LICENSE" "publish\LICENSE" >nul
)
if not exist "publish\LICENSE" (
	echo ERROR: LICENSE file missing from publish output.
	exit /b 1
)

:: Include the pinned runtime data release through the shared verified installer
set "DATA_DESTINATION=%~dp0publish\Data"
echo Adding pinned RatScanner data...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\setup-data.ps1" -DestinationPath "%DATA_DESTINATION%" -Force
if errorlevel 1 (
	echo Failed to download or validate RatScanner data.
	exit /b 1
)

:: Zip into a single overwriteable artifact
where 7z >nul 2>&1
if errorlevel 1 (
	echo 7-Zip not found; packing with Compress-Archive instead...
	powershell -NoProfile -Command "Compress-Archive -Path '.\publish\*' -DestinationPath 'RatScanner.zip' -Force"
	if errorlevel 1 (
		echo Failed to create RatScanner.zip.
		exit /b 1
	)
) else (
	echo Packing publish folder into RatScanner.zip...
	7z a -r RatScanner.zip ./publish/*
	if errorlevel 1 (
		echo Failed to create RatScanner.zip.
		exit /b 1
	)
)
if not exist "RatScanner.zip" (
	echo Failed to create RatScanner.zip.
	exit /b 1
)

:: Verify the packaged archive itself, not just the staged publish tree
echo Verifying release package...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\verify-package.ps1" -PackagePath "%~dp0RatScanner.zip"
if errorlevel 1 (
	echo ERROR: RatScanner.zip failed release package verification.
	exit /b 1
)

:: Finalize publish
echo Done
echo Output folder: publish\
echo Artifact zip:  RatScanner.zip
echo Run publish\RatScanner.exe to test locally.
echo.
echo Tip: for day-to-day coding use dev.bat (watch mode), not publish.bat.
endlocal
