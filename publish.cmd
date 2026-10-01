@echo off
REM ============================================================
REM  publish.cmd - one-shot release for Ocdup
REM
REM  Steps: unit tests (Release) -> dotnet publish -> publish\
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

tasklist /FI "IMAGENAME eq ocdup.exe" 2>nul | find /I "ocdup.exe" >nul
if not errorlevel 1 (
  echo [publish] WARNING: ocdup.exe is currently running - the publish step
  echo [publish] will fail on locked files. Fully exit DeepSeek Harness first.
  echo [publish] Aborting. See header comment for the correct order.
  exit /b 2
)

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

echo [publish] done: %CD%\publish
echo [publish] start DeepSeek Harness now - new sessions load this build.
exit /b 0
