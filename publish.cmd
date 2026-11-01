@echo off
REM ============================================================
REM  publish.cmd - one-shot release for Ocdup
REM
REM  Steps: clean-tree check -> unit tests (Release) -> dotnet publish
REM         -> smoke (ocdup.exe --version) -> publish\
REM
REM  IMPORTANT ORDER (learned the hard way):
REM  A running DeepSeek Harness hosts ocdup.exe and LOCKS
REM  publish\ocdup.exe/dll (it also auto-restarts the MCP child
REM  if you kill it). So:
REM    1. FULLY EXIT DeepSeek Harness
REM    2. run this script
REM    3. start DeepSeek Harness again
REM
REM  NOTE: keep this file ASCII-only (cmd.exe parses in OEM codepage).
REM ============================================================

setlocal EnableExtensions
cd /d "%~dp0"

REM v0.4.38: findstr, not find - when this script is launched from a Git-Bash-derived
REM PATH, cmd resolves "find" to GNU find and its switches (/I, /C) get read as PATHS,
REM silently breaking the lock check (fail-open) or recursing the whole drive (hang).
tasklist /FI "IMAGENAME eq ocdup.exe" 2>nul | findstr /I /C:"ocdup.exe" >nul
if not errorlevel 1 (
  echo [publish] WARNING: ocdup.exe is currently running - the publish step
  echo [publish] will fail on locked files. Fully exit DeepSeek Harness first.
  echo [publish] Aborting. See header comment for the correct order.
  exit /b 2
)

echo [publish] checking the working tree is clean (release what is committed)...
git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
  echo [publish] WARNING: git not available - skipping the clean-tree check.
  goto :tests
)
set DIRTY=0
for /f "delims=" %%l in ('git status --porcelain 2^>nul') do set /a DIRTY+=1
if "%DIRTY%"=="0" goto :tests
echo [publish] ABORT: the working tree has %DIRTY% uncommitted change(s) - the release would
echo [publish] contain a build that does not exist in the repo. Commit or stash first.
exit /b 3

:tests
echo [publish] unit tests (Release)...
dotnet test Ocdup.sln -c Release --nologo -v q
if errorlevel 1 (
  echo [publish] tests FAILED - aborting.
  exit /b 1
)

echo [publish] dotnet publish -c Release -o publish...
dotnet publish src\Ocdup\Ocdup.csproj -c Release -o publish --nologo -v q
if errorlevel 1 (
  echo [publish] publish FAILED.
  exit /b 1
)

echo [publish] smoke: publish\ocdup.exe --version
set VER=
for /f "delims=" %%v in ('publish\ocdup.exe --version 2^>nul') do (
  if not defined VER set VER=%%v
)
if not defined VER (
  echo [publish] smoke FAILED - the published binary did not report a version. Aborting.
  exit /b 1
)
set SHA=
for /f %%h in ('git rev-parse --short HEAD 2^>nul') do set SHA=%%h
echo [publish] done: %CD%\publish  [%VER% @ commit %SHA%]
echo [publish] start DeepSeek Harness now - new sessions load this build.
exit /b 0
