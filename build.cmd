@echo off
rem ===================================================================
rem  AutoZip - local build and test
rem
rem  Usage:
rem    build.cmd              Debug build + all unit tests
rem    build.cmd release      Release build + all unit tests
rem    build.cmd selftest     Debug build + unit tests + UI self-test
rem
rem  NOTE: keep this file ASCII-only - see the comment in publish.cmd.
rem ===================================================================
setlocal

set CONFIG=Debug
set SELFTEST=0
if /i "%~1"=="release"  set CONFIG=Release
if /i "%~1"=="selftest" set SELFTEST=1

rem NuGet goes through the local proxy on this machine. Do not override
rem values the caller already set, and leave it alone on CI - a runner has
rem no proxy on 127.0.0.1, and restore failing against a dead local port
rem reads like a network outage. GitHub Actions sets CI=true.
if "%CI%"=="" (
  if "%HTTPS_PROXY%"=="" set HTTPS_PROXY=http://127.0.0.1:10808
  if "%HTTP_PROXY%"==""  set HTTP_PROXY=http://127.0.0.1:10808
)

pushd "%~dp0"

echo.
echo [1/3] Building (%CONFIG%)...
dotnet build NewAutoZip.sln -c %CONFIG% --nologo
if errorlevel 1 goto :failed

echo.
echo [2/3] Unit tests...
dotnet test tests\NewAutoZip.Tests\NewAutoZip.Tests.csproj -c %CONFIG% --nologo --no-build
if errorlevel 1 goto :failed

rem  The project folders are still named NewAutoZip.* (internal code layout);
rem  the produced executable is AutoZip.exe - see AssemblyName in the csproj.
set APPDIR=src\NewAutoZip.App\bin\%CONFIG%\net8.0-windows
set APPEXE=%APPDIR%\AutoZip.exe

if "%SELFTEST%"=="0" (
  echo.
  echo [3/3] UI self-test skipped. Run "build.cmd selftest" to include it.
  goto :ok
)

echo.
echo [3/3] UI self-test...
rem  Walks every page and every settings control off-screen with WPF binding
rem  traces captured, so a broken XAML binding fails here instead of shipping.
"%APPEXE%" --selftest
if errorlevel 1 (
  echo.
  echo *** UI self-test failed. See %APPDIR%\selftest-report.txt ***
  goto :failed
)

:ok
echo.
echo Done. Executable: %APPEXE%
popd
endlocal
exit /b 0

:failed
echo.
echo *** BUILD FAILED. See the errors above. ***
popd
endlocal
exit /b 1
