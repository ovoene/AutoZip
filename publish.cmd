@echo off
rem ===================================================================
rem  AutoZip - self-contained single-file publish
rem
rem  Output:  publish\AutoZip.exe      (target machine needs no .NET)
rem           publish\tools\7za.exe    (must stay a real file on disk)
rem
rem  Usage:   publish.cmd              tests + publish + UI self-test
rem           publish.cmd noselftest  tests + publish, no UI self-test
rem
rem  "noselftest" exists for the release workflow, which runs on a GitHub
rem  runner. The self-test measures rendered colours and layout on a real
rem  desktop; on a CI machine its verdict is worth less than the flake it
rem  would add. release.bat runs the full gate on the dev machine before
rem  it tags, so nothing ships without the self-test having passed once.
rem  Do not reach for this flag by hand.
rem
rem  NOTE: keep this file ASCII-only. cmd.exe decodes batch files using
rem  the console codepage (936 here), not UTF-8, so non-ASCII comments
rem  get mis-decoded and can swallow the CR that ends the line, gluing
rem  the next command onto the comment. Chinese docs live in README.md.
rem ===================================================================
setlocal

set SELFTEST=1
if /i "%~1"=="noselftest" set SELFTEST=0

rem  NuGet goes through a local proxy on this machine. A CI runner has no
rem  such proxy, and pointing restore at a dead 127.0.0.1 there fails in a
rem  way that reads like a network outage. GitHub Actions sets CI=true.
if "%CI%"=="" (
  if "%HTTPS_PROXY%"=="" set HTTPS_PROXY=http://127.0.0.1:10808
  if "%HTTP_PROXY%"==""  set HTTP_PROXY=http://127.0.0.1:10808
)

pushd "%~dp0"

echo.
echo [1/4] Unit tests (no publish if they fail)...
dotnet test tests\NewAutoZip.Tests\NewAutoZip.Tests.csproj -c Release --nologo
if errorlevel 1 goto :failed

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
if errorlevel 1 goto :failed

if not exist "publish\AutoZip.exe" (
  echo.
  echo *** publish\AutoZip.exe is missing. ***
  goto :failed
)

rem  7za.exe is launched as a process, so it must exist as a real file.
rem  Single-file publish bundles Content into the exe unless the item sets
rem  ExcludeFromSingleFile - see NewAutoZip.App.csproj.
if not exist "publish\tools\7za.exe" (
  echo.
  echo *** publish\tools\7za.exe is missing - packing would fail. ***
  echo *** Check the Content item / ExcludeFromSingleFile in the csproj. ***
  goto :failed
)

echo.
if "%SELFTEST%"=="0" (
  echo [4/4] UI self-test skipped - "noselftest" was passed.
  goto :cleanup
)

echo [4/4] UI self-test on the published build...
rem  Verifies the single-file exe actually starts and every XAML binding
rem  resolves. Binding errors do not fail the compile, so this is the gate.
publish\AutoZip.exe --selftest
if errorlevel 1 (
  echo.
  echo *** UI self-test failed. See publish\selftest-report.txt ***
  goto :failed
)
if exist "publish\selftest-report.txt" del /q "publish\selftest-report.txt"

:cleanup
rem  The self-test runs the real app, so it creates the runtime data layout
rem  (logs\, ZipTemp\) right next to the exe - data lives in the program
rem  directory by design. Those belong to THIS machine, not to the target,
rem  so wipe them: the folder we hand over must contain nothing but the
rem  program itself. A log from the build machine in a fresh install is
rem  actively misleading when someone later goes looking for the first run.
rem  The release workflow zips this folder, so a stray file here would go
rem  out to every user.
if exist "publish\logs"          rmdir /s /q "publish\logs"
if exist "publish\ZipTemp"       rmdir /s /q "publish\ZipTemp"
if exist "publish\settings.json" del /q "publish\settings.json"
if exist "publish\state.json"    del /q "publish\state.json"

echo.
echo Done. Publish contents:
dir /b publish
echo.
echo Copy the whole "publish" folder to the target machine. No install needed.
popd
endlocal
exit /b 0

:failed
echo.
echo *** PUBLISH FAILED. ***
popd
endlocal
exit /b 1
