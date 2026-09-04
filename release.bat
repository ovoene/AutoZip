@echo off
setlocal

rem =====================================================================
rem  AutoZip - release launcher (this file is pure ASCII, no Chinese bytes)
rem
rem  Why pure ASCII:
rem    cmd.exe decodes a .bat using the console code page. On a UTF-8
rem    console (65001) a GBK-encoded Chinese file is mis-decoded into
rem    garbage commands (e.g. the 'ho' error). Pure ASCII is decoded
rem    identically on every code page, so the launcher always runs.
rem    All Chinese prompts and the whole release flow live in release.ps1
rem    (UTF-8 with BOM), which handles encoding correctly.
rem
rem  Usage:
rem    release.bat                auto-detect first-commit vs normal release
rem    release.bat 6.6.7          normal release with an explicit version
rem    release.bat -DryRun        normal release dry run
rem =====================================================================

set "REPO=%~dp0"
set "PS1=%~dp0release.ps1"

rem ---- Step 1: detect first-commit with ASCII-safe git commands ----
git rev-parse --is-inside-work-tree >nul 2>nul
if "%errorlevel%"=="0" (set IS_REPO=1) else (set IS_REPO=0)

git rev-parse -q --verify HEAD >nul 2>nul
if "%errorlevel%"=="0" (set HAS_HEAD=1) else (set HAS_HEAD=0)

set "FIRST=0"
if "%IS_REPO%"=="0" set FIRST=1
if "%HAS_HEAD%"=="0" set FIRST=1

if "%FIRST%"=="1" (set "ARGS=-FirstCommit") else (set "ARGS=")

rem ---- Step 2: make sure the engine script exists ----
if not exist "%PS1%" (
    echo [ERROR] Cannot find release.ps1 next to release.bat.
    echo         Keep release.ps1 and release.bat in the same directory.
    exit /b 1
)

rem ---- Step 3: run the engine (prefer pwsh, fall back to powershell) ----
where pwsh >nul 2>nul
if not errorlevel 1 (
    pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %ARGS% %*
    exit /b %errorlevel%
)

where powershell >nul 2>nul
if not errorlevel 1 (
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %ARGS% %*
    exit /b %errorlevel%
)

echo [ERROR] No PowerShell found on this machine.
echo         Windows ships with Windows PowerShell; this should not happen.
exit /b 1
