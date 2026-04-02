@echo off
:: Build a self-contained release ZIP for GitHub distribution
:: Reads version from Directory.Build.props

setlocal
set REPO=%~dp0

:: Read version from Directory.Build.props
for /f "tokens=3 delims=<>" %%a in ('findstr "<Version>" "%REPO%Directory.Build.props"') do set VERSION=%%a
if "%VERSION%"=="" (
    echo ERROR: Could not read Version from Directory.Build.props
    exit /b 1
)

set PUBLISH_DIR=%REPO%publish\RaisinTerminal
set ZIP_NAME=RaisinTerminal-v%VERSION%-win-x64.zip
set ZIP_PATH=%REPO%publish\%ZIP_NAME%

echo === Building RaisinTerminal v%VERSION% ===

:: Clean previous publish output
if exist "%REPO%publish" rmdir /s /q "%REPO%publish"

:: Publish self-contained for win-x64 using NuGet packages
echo Publishing...
dotnet publish "%REPO%RaisinTerminal\RaisinTerminal.csproj" -c Release -r win-x64 --self-contained -p:UseProjectReferences=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfContained=true -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo Publish failed!
    exit /b 1
)

:: Remove PDB files (not needed in release, can rebuild from tagged source)
del /q "%PUBLISH_DIR%\*.pdb" 2>nul

:: Create ZIP
echo Creating %ZIP_NAME%...
powershell -NoProfile -Command "Compress-Archive -Path '%PUBLISH_DIR%\*' -DestinationPath '%ZIP_PATH%' -Force"
if errorlevel 1 (
    echo ZIP creation failed!
    exit /b 1
)

echo.
echo === Done ===
echo Output: %ZIP_PATH%
echo.
echo To create a GitHub release:
echo   gh release create v%VERSION% "%ZIP_PATH%" --repo gerleim/RaisinTerminal --title "v%VERSION%" --notes "Release v%VERSION%"
