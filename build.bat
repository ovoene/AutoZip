@echo off
rem ===================================================================
rem  AutoZip - one entry point: build, test, publish, release
rem
rem  Double-click it, or run it with no arguments, for a numbered menu.
rem  With arguments it runs exactly one step and exits with its result.
rem  That is how the release workflow (CI) and release.ps1 call it, so
rem  the menu, CI and the release gate all run the very same commands:
rem
rem    build.bat build                Debug build + all unit tests
rem    build.bat build release        Release build + all unit tests
rem    build.bat build selftest       Debug build + unit tests + UI self-test
rem    build.bat publish              tests + single-file publish + UI self-test
rem    build.bat publish 6.6.8        same, but write 6.6.8 into
rem                                   Directory.Build.props first
rem    build.bat publish noselftest   tests + publish, no UI self-test (CI)
rem    build.bat release [args]       release to GitHub through release.ps1;
rem                                   args go to it unchanged: 6.6.7,
rem                                   -DryRun, -NoSelfTest, -NoGate,
rem                                   -ShowGitOutput
rem
rem  This file replaces build.cmd, publish.cmd and release.bat.
rem
rem  NOTE: keep this file ASCII-only. cmd.exe decodes batch files using
rem  the console codepage (936 here), not UTF-8, so non-ASCII text gets
rem  mis-decoded and can swallow the CR that ends the line, gluing the
rem  next command onto the comment. That is why the menu is English;
rem  the release flow itself lives in release.ps1 (UTF-8 with BOM) and
rem  speaks Chinese. Keep CRLF line endings too (see .gitattributes):
rem  goto and multi-line parenthesised blocks misparse on LF-only files.
rem ===================================================================
setlocal EnableExtensions

rem  NuGet goes through the local proxy on this machine. Do not override
rem  values the caller already set, and leave it alone on CI - a runner
rem  has no proxy on 127.0.0.1, and restore failing against a dead local
rem  port reads like a network outage. GitHub Actions sets CI=true.
if "%CI%"=="" (
  if "%HTTPS_PROXY%"=="" set HTTPS_PROXY=http://127.0.0.1:10808
  if "%HTTP_PROXY%"==""  set HTTP_PROXY=http://127.0.0.1:10808
)

rem  Captured once, up here: inside a "call :label" subroutine %~dp0 is
rem  resolved against the label, and goes wrong when the script was started
rem  through a relative path.
set "AZ_HERE=%~dp0"
pushd "%AZ_HERE%"

if "%~1"=="" goto :menu

set "AZ_STEP=%~1"
shift

if /i "%AZ_STEP%"=="build"   goto :cli_build
if /i "%AZ_STEP%"=="publish" goto :cli_publish
if /i "%AZ_STEP%"=="release" goto :cli_release
if /i "%AZ_STEP%"=="help"    goto :usage
if    "%AZ_STEP%"=="/?"      goto :usage

rem  The unknown word is not echoed back: a stray & or | in it would be
rem  executed by the echo line itself.
echo.
echo *** Unknown step. ***
goto :usage_failed

rem ===================================================================
rem  Command line
rem ===================================================================

:cli_build
set "AZ_CONFIG=Debug"
set "AZ_SELFTEST=0"
if /i "%~1"=="release"  set "AZ_CONFIG=Release"
if /i "%~1"=="selftest" set "AZ_SELFTEST=1"
call :do_build
goto :finish

:cli_publish
rem  Arguments in any order: "noselftest" and/or a version like 6.6.8.
rem  Anything that is not "noselftest" is taken as a version and checked
rem  later - an unrecognised argument must not be silently dropped, or
rem  "build.bat publish 6.6.8" would quietly build the old version.
set "AZ_SELFTEST=1"
set "AZ_SET_VERSION="

:publish_args
if "%~1"=="" goto :publish_go
if /i "%~1"=="noselftest" goto :publish_noselftest
set "AZ_SET_VERSION=%~1"
shift
goto :publish_args

:publish_noselftest
set "AZ_SELFTEST=0"
shift
goto :publish_args

:publish_go
call :do_publish
goto :finish

:cli_release
rem  shift does not touch %*, so collect what is left by hand.
set "AZ_RELEASE_ARGS="

:release_args
if "%~1"=="" goto :release_go
set "AZ_RELEASE_ARGS=%AZ_RELEASE_ARGS% %1"
shift
goto :release_args

:release_go
call :do_release %AZ_RELEASE_ARGS%
goto :finish

:finish
set "AZ_RC=%errorlevel%"
popd
endlocal & exit /b %AZ_RC%

:usage
call :usage_text
popd
endlocal
exit /b 0

:usage_failed
call :usage_text
popd
endlocal
exit /b 2

:usage_text
echo.
echo Usage:
echo   build.bat                       interactive menu
echo   build.bat build [release^|selftest]
echo   build.bat publish [X.Y.Z] [noselftest]
echo   build.bat release [X.Y.Z] [-DryRun] [-NoSelfTest] [-NoGate] [-ShowGitOutput]
exit /b 0

rem ===================================================================
rem  Menu
rem ===================================================================

:menu
rem  A runner has nobody to answer the prompt; waiting for one would hang
rem  the job until its timeout. CI always passes a step.
if not "%CI%"=="" goto :usage_failed

echo.
echo  ==========================================================
echo    AutoZip  -  build / publish / release
echo  ==========================================================
echo.
echo    1   Build (Debug)    + unit tests
echo    2   Build (Release)  + unit tests
echo    3   Build (Debug)    + unit tests + UI self-test
echo.
echo    4   Publish locally  : tests, single-file exe, UI self-test
echo    5   Publish locally, writing a new version number first
echo.
echo    6   Release to GitHub  (interactive, Chinese prompts)
echo    7   Release dry run    (says what it would do, changes nothing)
echo.
echo    0   Exit
echo.

rem  choice instead of set /p: one keystroke, and nothing the user types
rem  can ever reach a command line. Errorlevel is the key's position.
choice /c 12345670 /n /m "  Choose 1-7 (0 = exit): "
set "AZ_PICK=%errorlevel%"

if "%AZ_PICK%"=="1" goto :menu_1
if "%AZ_PICK%"=="2" goto :menu_2
if "%AZ_PICK%"=="3" goto :menu_3
if "%AZ_PICK%"=="4" goto :menu_4
if "%AZ_PICK%"=="5" goto :menu_5
if "%AZ_PICK%"=="6" goto :menu_6
if "%AZ_PICK%"=="7" goto :menu_7
goto :menu_exit

:menu_1
set "AZ_CONFIG=Debug"
set "AZ_SELFTEST=0"
call :do_build
goto :menu_done

:menu_2
set "AZ_CONFIG=Release"
set "AZ_SELFTEST=0"
call :do_build
goto :menu_done

:menu_3
set "AZ_CONFIG=Debug"
set "AZ_SELFTEST=1"
call :do_build
goto :menu_done

:menu_4
set "AZ_SELFTEST=1"
set "AZ_SET_VERSION="
call :do_publish
goto :menu_done

:menu_5
rem  Whatever is typed only ever travels through the environment and is
rem  validated by PowerShell before anything uses it (see :do_publish).
set "AZ_SET_VERSION="
echo.
set /p "AZ_SET_VERSION=  New version (X.Y.Z, e.g. 6.7.1), empty = back to menu: "
if not defined AZ_SET_VERSION goto :menu
set "AZ_SELFTEST=1"
call :do_publish
goto :menu_done

:menu_6
call :do_release
goto :menu_done

:menu_7
call :do_release -DryRun
goto :menu_done

:menu_done
if errorlevel 1 goto :menu_failed
echo.
echo  ---- Finished OK. ----
goto :menu_pause

:menu_failed
echo.
echo  ---- FAILED. See the messages above. ----

:menu_pause
echo.
pause
goto :menu

:menu_exit
popd
endlocal
exit /b 0

rem ===================================================================
rem  Step: build + unit tests [+ UI self-test]
rem  In:  AZ_CONFIG (Debug/Release), AZ_SELFTEST (0/1)
rem ===================================================================

:do_build
echo.
echo [1/3] Building (%AZ_CONFIG%)...
dotnet build NewAutoZip.sln -c %AZ_CONFIG% --nologo
if errorlevel 1 goto :build_failed

echo.
echo [2/3] Unit tests...
dotnet test tests\NewAutoZip.Tests\NewAutoZip.Tests.csproj -c %AZ_CONFIG% --nologo --no-build
if errorlevel 1 goto :build_failed

rem  The project folders are still named NewAutoZip.* (internal code
rem  layout); the produced executable is AutoZip.exe - see AssemblyName.
set "AZ_APPDIR=src\NewAutoZip.App\bin\%AZ_CONFIG%\net8.0-windows"

if "%AZ_SELFTEST%"=="1" goto :build_selftest
echo.
echo [3/3] UI self-test skipped. Menu item 3 or "build.bat build selftest" includes it.
goto :build_ok

:build_selftest
echo.
echo [3/3] UI self-test...
rem  Walks every page and every settings control off-screen with WPF
rem  binding traces captured, so a broken XAML binding fails here instead
rem  of shipping. It prints nothing; the verdict is the exit code and the
rem  report file. Delete the old report first so a stale one cannot be
rem  mistaken for this run's.
if exist "%AZ_APPDIR%\selftest-report.txt" del /q "%AZ_APPDIR%\selftest-report.txt"
"%AZ_APPDIR%\AutoZip.exe" --selftest
if errorlevel 1 goto :build_selftest_failed

:build_ok
echo.
echo Done. Executable: %AZ_APPDIR%\AutoZip.exe
exit /b 0

:build_selftest_failed
echo.
echo *** UI self-test failed. See %AZ_APPDIR%\selftest-report.txt ***

:build_failed
echo.
echo *** BUILD FAILED. See the errors above. ***
exit /b 1

rem ===================================================================
rem  Step: self-contained single-file publish
rem  In:  AZ_SELFTEST (0/1), AZ_SET_VERSION (empty = keep the version)
rem
rem  Output:  publish\AutoZip.exe      (target machine needs no .NET)
rem           publish\tools\7za.exe    (must stay a real file on disk)
rem
rem  With no version Directory.Build.props is not touched at all. The
rem  version is written BEFORE the gates run, so a run that dies at the
rem  unit tests leaves the new version in the file - release.ps1 behaves
rem  the same way. This is a convenience for local builds only: it does
rem  not commit, tag or push anything; real releases go through menu
rem  item 6 / "build.bat release", which owns the git side.
rem
rem  "noselftest" exists for the release workflow, which runs on a GitHub
rem  runner. The self-test measures rendered colours and layout on a real
rem  desktop; on a CI machine its verdict is worth less than the flake it
rem  would add. The release flow runs the full gate on the dev machine
rem  before it tags, so nothing ships without the self-test having passed
rem  once. Do not reach for this flag by hand.
rem ===================================================================

:do_publish
if not defined AZ_SET_VERSION goto :publish_noversion

echo.
echo [version] Writing the new version into Directory.Build.props ...
rem  Batch has no regex, so PowerShell does the edit (it is on every
rem  supported Windows). The version travels in through the environment,
rem  never through the command line, so nothing inside it can come back
rem  as code - and it is not echoed here before it has been validated,
rem  for the same reason. Same three properties, same patterns, same BOM
rem  handling as release.ps1 - if you change one, change the other.
rem  Quoted "set" is required: the patterns contain < and >, which cmd
rem  would otherwise read as redirection.
set "PSV=$ErrorActionPreference='Stop';"
set "PSV=%PSV% $v=$env:AZ_SET_VERSION;"
set "PSV=%PSV% if ($v -notmatch '^v?\d+\.\d+\.\d+$') { Write-Host ('  *** Not a version: ' + $v + ' - expected X.Y.Z, for example 6.6.8 ***'); exit 2 };"
set "PSV=%PSV% $v=$v.TrimStart('v');"
set "PSV=%PSV% $f=Join-Path (Get-Location) 'Directory.Build.props';"
set "PSV=%PSV% $b=[System.IO.File]::ReadAllBytes($f);"
set "PSV=%PSV% $bom=($b.Length -ge 3 -and $b[0] -eq 239 -and $b[1] -eq 187 -and $b[2] -eq 191);"
set "PSV=%PSV% $t=[System.Text.Encoding]::UTF8.GetString($b); if ($bom) { $t=$t.Substring(1) };"
set "PSV=%PSV% $t=[regex]::Replace($t,'<Version>[^<]*</Version>','<Version>'+$v+'</Version>');"
set "PSV=%PSV% $t=[regex]::Replace($t,'<AssemblyVersion>[^<]*</AssemblyVersion>','<AssemblyVersion>'+$v+'.0</AssemblyVersion>');"
set "PSV=%PSV% $t=[regex]::Replace($t,'<FileVersion>[^<]*</FileVersion>','<FileVersion>'+$v+'.0</FileVersion>');"
set "PSV=%PSV% [System.IO.File]::WriteAllText($f,$t,(New-Object System.Text.UTF8Encoding $bom));"
set "PSV=%PSV% $e=[regex]::Escape($v); $back=[System.IO.File]::ReadAllText($f);"
set "PSV=%PSV% if (-not (($back -match ('<Version>'+$e+'</Version>')) -and ($back -match ('<AssemblyVersion>'+$e+'\.0</AssemblyVersion>')) -and ($back -match ('<FileVersion>'+$e+'\.0</FileVersion>')))) { Write-Host '  *** Directory.Build.props did not take the new version. ***'; exit 3 };"
set "PSV=%PSV% Write-Host ('  Version / AssemblyVersion / FileVersion -> ' + $v)"
powershell -NoProfile -ExecutionPolicy Bypass -Command "%PSV%"
if errorlevel 1 goto :publish_failed

:publish_noversion
echo.
echo [1/4] Unit tests (no publish if they fail)...
dotnet test tests\NewAutoZip.Tests\NewAutoZip.Tests.csproj -c Release --nologo
if errorlevel 1 goto :publish_failed

echo.
echo [2/4] Cleaning previous publish output...
if exist publish rmdir /s /q publish

echo.
echo [3/4] Publishing...
rem  PublishSingleFile                one exe
rem  self-contained                   no .NET runtime needed on target
rem  IncludeNativeLibrariesForSelf... bundle WPF native deps into the exe
rem  PublishReadyToRun                precompile; noticeably faster cold start
dotnet publish src\NewAutoZip.App\NewAutoZip.App.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:PublishReadyToRun=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -o publish ^
  --nologo
if errorlevel 1 goto :publish_failed

if exist "publish\AutoZip.exe" goto :publish_have_exe
echo.
echo *** publish\AutoZip.exe is missing. ***
goto :publish_failed

:publish_have_exe
rem  7za.exe is launched as a process, so it must exist as a real file.
rem  Single-file publish bundles Content into the exe unless the item sets
rem  ExcludeFromSingleFile - see NewAutoZip.App.csproj.
if exist "publish\tools\7za.exe" goto :publish_have_7za
echo.
echo *** publish\tools\7za.exe is missing - packing would fail. ***
echo *** Check the Content item / ExcludeFromSingleFile in the csproj. ***
goto :publish_failed

:publish_have_7za
echo.
if "%AZ_SELFTEST%"=="1" goto :publish_selftest
echo [4/4] UI self-test skipped - "noselftest" was passed.
goto :publish_cleanup

:publish_selftest
echo [4/4] UI self-test on the published build...
rem  Verifies the single-file exe actually starts and every XAML binding
rem  resolves. Binding errors do not fail the compile, so this is the gate.
publish\AutoZip.exe --selftest
if errorlevel 1 goto :publish_selftest_failed
if exist "publish\selftest-report.txt" del /q "publish\selftest-report.txt"

:publish_cleanup
rem  The self-test runs the real app, so it creates the runtime data layout
rem  (logs\, ZipTemp\) right next to the exe - data lives in the program
rem  directory by design. Those belong to THIS machine, not to the target,
rem  so wipe them: the folder we hand over must contain nothing but the
rem  program itself. The release workflow zips this folder, so a stray
rem  file here would go out to every user.
if exist "publish\logs"          rmdir /s /q "publish\logs"
if exist "publish\ZipTemp"       rmdir /s /q "publish\ZipTemp"
if exist "publish\settings.json" del /q "publish\settings.json"
if exist "publish\state.json"    del /q "publish\state.json"

echo.
echo Done. Publish contents:
dir /b publish
echo.
echo Copy the whole "publish" folder to the target machine. No install needed.
exit /b 0

:publish_selftest_failed
echo.
echo *** UI self-test failed. See publish\selftest-report.txt ***

:publish_failed
echo.
echo *** PUBLISH FAILED. ***
exit /b 1

rem ===================================================================
rem  Step: release to GitHub (release.ps1 does the work)
rem  In:  arguments for release.ps1, passed through unchanged
rem
rem  First-commit detection happens here with ASCII-only git commands and
rem  is handed over as -FirstCommit; release.ps1 checks again itself. The
rem  two cases: not a repository yet, or a repository whose current branch
rem  has no commit (fresh init, or an --orphan branch).
rem ===================================================================

:do_release
if exist "%AZ_HERE%release.ps1" goto :release_have_script
echo.
echo [ERROR] Cannot find release.ps1 next to build.bat.
echo         Keep release.ps1 and build.bat in the same directory.
exit /b 1

:release_have_script
set "AZ_FIRST="
git rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 set "AZ_FIRST=-FirstCommit"
git rev-parse -q --verify HEAD >nul 2>nul
if errorlevel 1 set "AZ_FIRST=-FirstCommit"

rem  Prefer PowerShell 7, fall back to Windows PowerShell. No parentheses
rem  around the calls: inside a ( ) block %errorlevel% is expanded before
rem  the command runs, which made the old release.bat always exit 0.
where pwsh >nul 2>nul
if errorlevel 1 goto :release_try_windows_powershell
pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%AZ_HERE%release.ps1" %AZ_FIRST% %*
exit /b

:release_try_windows_powershell
where powershell >nul 2>nul
if errorlevel 1 goto :release_no_powershell
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%AZ_HERE%release.ps1" %AZ_FIRST% %*
exit /b

:release_no_powershell
echo [ERROR] No PowerShell found on this machine.
echo         Windows ships with Windows PowerShell; this should not happen.
exit /b 1
