@echo off
setlocal

:: Resolve every path below against the repository root, not the caller's working directory
pushd "%~dp0"

:: Always replace the same local outputs so repeated builds do not accumulate folders/zips
echo Removing old publish folder and zip...
if exist publish rmdir /s /q publish
if exist RatScanner.zip del /f /q RatScanner.zip

:: Publish (matches CI: Release, win-x64, single-file, self-contained)
echo Publishing RatScanner project...
dotnet publish src/App/RatScanner.csproj -c Release -o publish --runtime win-x64 -p:PublishSingleFile=true --self-contained true
if errorlevel 1 (
	echo Publish failed.
	goto :fail
)

:: Ensure LICENSE is always present for redistributed packages (license notice requirement)
if not exist "publish\LICENSE" (
	if exist "LICENSE" copy /y "LICENSE" "publish\LICENSE" >nul
)
if not exist "publish\LICENSE" (
	echo ERROR: LICENSE file missing from publish output.
	goto :fail
)

:: Include the pinned runtime data release through the shared verified installer
echo Adding pinned RatScanner data...
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\setup-data.ps1" -DestinationPath "%CD%\publish\Data" -Force
if errorlevel 1 (
	echo Failed to download or validate RatScanner data.
	goto :fail
)

:: Zip into a single overwriteable artifact
where 7z >nul 2>&1
if errorlevel 1 (
	echo 7-Zip not found; packing with Compress-Archive instead...
	powershell -NoProfile -Command "Compress-Archive -Path '.\publish\*' -DestinationPath 'RatScanner.zip' -Force"
	if errorlevel 1 (
		echo Failed to create RatScanner.zip.
		goto :fail
	)
) else (
	echo Packing publish folder into RatScanner.zip...
	7z a -r RatScanner.zip ./publish/*
	if errorlevel 1 (
		echo Failed to create RatScanner.zip.
		goto :fail
	)
)
if not exist "RatScanner.zip" (
	echo Failed to create RatScanner.zip.
	goto :fail
)

:: Verify the packaged archive itself, not just the staged publish tree
echo Verifying release package...
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\verify-package.ps1" -PackagePath "%CD%\RatScanner.zip"
if errorlevel 1 (
	echo ERROR: RatScanner.zip failed release package verification.
	goto :fail
)

:: Finalize publish
echo Done
echo Output folder: publish\
echo Artifact zip:  RatScanner.zip
echo Run publish\RatScanner.exe to test locally.
echo.
echo Tip: for day-to-day coding use dev.bat (watch mode), not publish.bat.
popd
endlocal
exit /b 0

:fail
popd
endlocal
exit /b 1
