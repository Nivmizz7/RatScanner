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

:: Include runtime data (use shared extractor that falls back if Expand-Archive is broken)
echo Adding latest RatScanner data...
curl -fL --retry 3 --retry-all-errors "https://github.com/RatScanner/RatScannerData/releases/latest/download/Data.zip" --output "publish/Data.zip"
if errorlevel 1 (
	echo Failed to download RatScanner data.
	exit /b 1
)
if exist publish\Data rmdir /s /q publish\Data
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Expand-Zip.ps1" -ArchivePath "%~dp0publish\Data.zip" -DestinationPath "%~dp0publish\Data"
if errorlevel 1 (
	echo Failed to extract RatScanner data.
	exit /b 1
)
del /f /q "publish\Data.zip"

:: If archive nested under Data/, flatten then validate the same package contract as CI.
if not exist "publish\Data\maps.json" (
	if exist "publish\Data\Data\maps.json" (
		echo Flattening nested Data\Data layout...
		robocopy "publish\Data\Data" "publish\Data" /E /MOVE >nul
		rem robocopy: 0-7 = success/partial; 8+ = failure. Use errorlevel form so block parse-time expansion is safe.
		if errorlevel 8 (
			echo Failed to flatten nested Data layout.
			exit /b 1
		)
		if exist "publish\Data\Data" rmdir /s /q "publish\Data\Data"
	)
)
if not exist "publish\Data\maps.json" (
	echo publish\Data looks incomplete after extract: missing maps.json.
	exit /b 1
)
if not exist "publish\Data\unknown.png" (
	echo publish\Data looks incomplete after extract: missing unknown.png.
	exit /b 1
)
if not exist "publish\Data\traineddata\eng.traineddata" (
	echo publish\Data looks incomplete after extract: missing traineddata\eng.traineddata.
	exit /b 1
)
dir /b "publish\Data\icons\*.png" >nul 2>&1
if errorlevel 1 (
	echo publish\Data looks incomplete after extract: no item icons found.
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

:: Finalize publish
echo Done
echo Output folder: publish\
echo Artifact zip:  RatScanner.zip
echo Run publish\RatScanner.exe to test locally.
echo.
echo Tip: for day-to-day coding use dev.bat (watch mode), not publish.bat.
endlocal
