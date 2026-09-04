@echo off
setlocal EnableExtensions
rem ---------------------------------------------------------------------------
rem  Aftermath - build, then run.
rem
rem  Usage:
rem    run.cmd                                  show the tool's own help
rem    run.cmd services --workspace c:\workspace\work
rem    run.cmd collect  --workspace c:\workspace\work --at 2026-07-17T13:00:00Z --window 24h
rem
rem  Set INCIDENTTIMELINE_CONFIG=Release to build and run the Release binary.
rem
rem  This deliberately launches the built .exe rather than "dotnet run": dotnet run
rem  installs its own Ctrl-C handling, which would sit between the console and the
rem  tool's cancellation path. The plan's cancellation check needs the real thing:
rem    run.cmd collect --workspace c:\workspace\work --at 2026-07-17T13:00:00Z --window 8760h
rem  then press Ctrl-C and confirm no orphaned git.exe is left behind.
rem ---------------------------------------------------------------------------

pushd "%~dp0"

set "CONFIG=%INCIDENTTIMELINE_CONFIG%"
if "%CONFIG%"=="" set "CONFIG=Debug"

set "SOLUTION=src\Aftermath.slnx"
set "EXE=src\Aftermath\bin\%CONFIG%\net10.0\Aftermath.exe"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [run.cmd] dotnet is not on PATH. Install the .NET 10 SDK, or open a shell that has it.
    popd
    exit /b 127
)

echo [run.cmd] building %SOLUTION% ^(%CONFIG%^)
dotnet build "%SOLUTION%" --configuration %CONFIG% --nologo --verbosity quiet
if errorlevel 1 (
    echo [run.cmd] build FAILED - not running.
    popd
    exit /b 1
)

if not exist "%EXE%" (
    echo [run.cmd] build succeeded but %EXE% is missing.
    popd
    exit /b 1
)

if "%~1"=="" (
    echo [run.cmd] no arguments given - showing the tool's own help.
    echo.
    "%EXE%" --help
    popd
    exit /b 0
)

echo [run.cmd] %EXE% %*
echo.
"%EXE%" %*
set "EXITCODE=%ERRORLEVEL%"

popd
exit /b %EXITCODE%
