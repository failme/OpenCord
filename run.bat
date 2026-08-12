@echo off
setlocal
cd /d "%~dp0"

echo == Closing any running ClaudeScord instance ==
taskkill /F /IM ClaudeScord.exe >nul 2>&1
if errorlevel 1 (
    echo    (none was running)
) else (
    echo    closed
)

echo == Building ==
set DOTNET_ROOT=C:\Users\natha\.dotnet
call "C:\Users\natha\.dotnet\dotnet.exe" build ClaudeScord.csproj -v:m -nologo
if errorlevel 1 (
    echo.
    echo Build FAILED - fix the errors above, then run this script again.
    pause
    exit /b 1
)

echo == Launching (--log writes debug.log next to the exe) ==
start "" "%~dp0bin\Debug\net8.0-windows\ClaudeScord.exe" --log
echo Done.
endlocal
