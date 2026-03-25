@echo off
:: Rebuild & Restart RaisinTerminal
:: Queries the running app via named pipe to check if sessions are busy.

set REPO=%~dp0
set DEST=%LOCALAPPDATA%\RaisinTerminal

:: Brief delay to let the launching Claude session go idle
%SystemRoot%\System32\timeout.exe /t 3 /nobreak >nul

:: Ask the running app if it's safe to restart
for /f "delims=" %%R in ('powershell -NoProfile -Command ^
  "try { $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.','RaisinTerminal.RebuildGate','InOut'); $pipe.Connect(2000); $sw = New-Object System.IO.StreamWriter($pipe); $sw.Write('canRestart'); $sw.Flush(); $pipe.WaitForPipeDrain(); $buf = New-Object byte[] 256; $n = $pipe.Read($buf,0,256); [System.Text.Encoding]::UTF8.GetString($buf,0,$n); $pipe.Close() } catch { 'UNREACHABLE' }"') do set GATE=%%R

if "%GATE%"=="OK" goto :proceed
if "%GATE%"=="UNREACHABLE" (
    echo RaisinTerminal is not running or pipe unavailable. Proceeding...
    goto :proceed
)

:: BUSY:n — extract count and warn
set BUSY_COUNT=%GATE:BUSY:=%
echo.
echo WARNING: %BUSY_COUNT% terminal session(s) have running commands.
echo Rebuilding will kill them.
echo.
choice /C YN /M "Proceed anyway?"
if errorlevel 2 exit /b 0

:proceed
echo Stopping RaisinTerminal...
:: Ask the app to quit gracefully (saves layout + session IDs)
for /f "delims=" %%Q in ('powershell -NoProfile -Command ^
  "try { $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.','RaisinTerminal.RebuildGate','InOut'); $pipe.Connect(2000); $sw = New-Object System.IO.StreamWriter($pipe); $sw.Write('quit'); $sw.Flush(); $pipe.WaitForPipeDrain(); $buf = New-Object byte[] 256; $n = $pipe.Read($buf,0,256); [System.Text.Encoding]::UTF8.GetString($buf,0,$n); $pipe.Close() } catch { 'FAILED' }"') do set QUIT=%%Q

:: Wait for the process to exit, fall back to taskkill
set /a WAIT=0
:waitloop
tasklist /fi "imagename eq RaisinTerminal.exe" 2>nul | %SystemRoot%\System32\find.exe /i "RaisinTerminal.exe" >nul
if errorlevel 1 goto :stopped
if %WAIT% geq 5 (
    echo Graceful shutdown timed out, force-killing...
    taskkill /f /im RaisinTerminal.exe >nul 2>&1
    %SystemRoot%\System32\timeout.exe /t 1 /nobreak >nul
    goto :stopped
)
set /a WAIT+=1
%SystemRoot%\System32\timeout.exe /t 1 /nobreak >nul
goto :waitloop
:stopped

echo Publishing...
dotnet publish "%REPO%RaisinTerminal\RaisinTerminal.csproj" -c Release -r win-x64 --self-contained -o "%DEST%"
if errorlevel 1 (
    echo Publish failed!
    pause
    exit /b 1
)

echo Starting RaisinTerminal...
start "" "%DEST%\RaisinTerminal.exe"
exit
