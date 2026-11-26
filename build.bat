@echo off
setlocal enabledelayedexpansion

set "REPO_DIR=%~dp0"
set "SBOX_DIR="

rem Allow passing s&box path as first argument (if not a flag like -p:...)
set "FIRST_ARG=%~1"
if defined FIRST_ARG (
    if not "!FIRST_ARG:~0,1!"=="-" (
        set "SBOX_DIR=%~1"
        shift
    )
)

rem Check environment variable or default Steam install path
if not defined SBOX_DIR if defined FACEPUNCH_ENGINE set "SBOX_DIR=%FACEPUNCH_ENGINE%"
if not defined SBOX_DIR if exist "%ProgramFiles(x86)%\Steam\steamapps\common\sbox\bin\managed\Sandbox.Engine.dll" set "SBOX_DIR=%ProgramFiles(x86)%\Steam\steamapps\common\sbox"
if defined SBOX_DIR goto :check_sbox

rem Auto-detect s&box from other Steam library folders
set "TMP_SBOX=%TEMP%\sbox-blam-find-sbox.txt"
powershell -NoProfile -Command "$s=(Get-ItemProperty 'HKCU:\Software\Valve\Steam' -EA 0).SteamPath; $c=@(); if($s){$s=$s -replace '/','\'; $c+=Join-Path $s 'steamapps\common\sbox'; $v=Join-Path $s 'steamapps\libraryfolders.vdf'; if(Test-Path $v){foreach($m in [regex]::Matches((Get-Content $v -Raw),'\"path\"\s+\"([^\"]+)\"')){$c+=Join-Path ($m.Groups[1].Value -replace '\\\\','\') 'steamapps\common\sbox'}}}; $c|Select-Object -Unique|Where-Object{Test-Path (Join-Path $_ 'bin\managed\Sandbox.Engine.dll')}|Select-Object -First 1" > "%TMP_SBOX%" 2>nul
set /p SBOX_DIR=<"%TMP_SBOX%"
del "%TMP_SBOX%" 2>nul

:check_sbox
if not defined SBOX_DIR (
    echo Could not find your s^&box Steam install automatically.
    echo Run: build.bat "D:\path\to\sbox"
    goto :fail
)

if not exist "!SBOX_DIR!\bin\managed\Sandbox.Engine.dll" (
    echo s^&box was not found at: !SBOX_DIR!
    echo Run: build.bat "D:\path\to\sbox"
    goto :fail
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo .NET 10 SDK is required to build this mount: https://dotnet.microsoft.com/
    goto :fail
)

rem Check if s&box or the editor is running (it locks mounts/blam/blam.dll)
tasklist /fi "imagename eq sbox.exe" 2>nul | find /i "sbox.exe" >nul
if not errorlevel 1 (
    echo s^&box is currently running and locks blam.dll. Close s^&box and run build.bat again.
    goto :fail
)
tasklist /fi "imagename eq sbox-dev.exe" 2>nul | find /i "sbox-dev.exe" >nul
if not errorlevel 1 (
    echo The s^&box editor is currently running and locks blam.dll. Close it and run build.bat again.
    goto :fail
)

echo Building and deploying Halo mounts to: %SBOX_DIR%\mounts\blam
echo ^(First build compiles all 48 shaders and takes a few minutes.^)
echo.

dotnet build "%REPO_DIR%src\Sandbox.Mounting.Blam" -p:DeployToSbox=true -p:SboxDir="%SBOX_DIR%" %1 %2 %3 %4 %5 %6 %7 %8 %9
if errorlevel 1 goto :fail

echo.
echo Done! Start s^&box and enable Halo: Combat Evolved, Halo 2, and/or Halo 3 in the mount list.
goto :done

:fail
echo.
echo Build failed.
if "%CI%"=="" if "%NO_PAUSE%"=="" pause
exit /b 1

:done
if "%CI%"=="" if "%NO_PAUSE%"=="" pause
exit /b 0
