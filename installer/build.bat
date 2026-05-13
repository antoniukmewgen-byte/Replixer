@echo off
setlocal

set ISCC="D:\Programs\Inno Setup 6\ISCC.exe"
set ROOT=%~dp0..

echo [1/2] Publishing Replixer...
dotnet publish "%ROOT%\Replixer.csproj" -c Release -r win-x64 --self-contained true ^
  -o "%ROOT%\publish"

if errorlevel 1 (
    echo ERROR: publish failed
    pause
    exit /b 1
)

echo [2/2] Building installer...
%ISCC% "%~dp0setup.iss"

if errorlevel 1 (
    echo ERROR: Inno Setup failed. Make sure Inno Setup 6 is installed.
    echo Download: https://jrsoftware.org/isdl.php
    pause
    exit /b 1
)

echo.
echo Done! Installer: %~dp0Replixer-Setup.exe
pause
