@echo off

:: Remove old publish folder
echo Removing old publish folder...
rmdir /s /q publish

:: Publish
echo Publishing RatScanner project...
dotnet publish RatScanner/RatScanner.csproj -c Release -o publish --runtime win-x64 -p:PublishSingleFile=true --self-contained true

:: Include runtime data
echo Adding latest RatScanner data...
curl -L "https://github.com/RatScanner/RatScannerData/releases/latest/download/Data.zip" --output "publish/Data.zip"
powershell -NoProfile -Command "Expand-Archive -LiteralPath 'publish\Data.zip' -DestinationPath 'publish\Data' -Force"
del "publish\Data.zip"

:: Zip
where 7z
if %ERRORLEVEL% neq 0 (
	echo Skipping packing since no 7-Zip installation was found in path
) else (
	echo Packing publish folder...
	7z a -r RatScanner.zip ./publish/*
)

:: Finalize publish
echo Done
echo Run the published version to make sure there were no errors
pause
