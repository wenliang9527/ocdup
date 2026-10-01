@echo off
REM ============================================================
REM  Deploy dsh-ocdup-panel (DeepSeek Harness ocdup debug panel)
REM
REM  Source of truth: THIS directory (repo tools\dsh-ocdup-panel)
REM  Targets:
REM    %USERPROFILE%\.dsh\profiles\desktop\dsh-ocdup-panel
REM    %USERPROFILE%\.dsh\profiles\desktop\node_modules\dsh-ocdup-panel
REM  Usage:
REM    deploy.cmd        deploy to the desktop profile (daily use)
REM    deploy.cmd web    also deploy to the web profile copies
REM  Restart DeepSeek Harness afterwards: the loader reads these files
REM  at startup (HMR is disabled for loader patches).
REM
REM  Uninstall: delete the target folders and remove the
REM  "id: dsh-ocdup-panel" insert block from
REM  %USERPROFILE%\.dsh\profiles\{desktop,web}\cordis.patch.yml
REM
REM  NOTE: keep this file ASCII-only. cmd.exe parses batch files in the
REM  OEM codepage (GBK here); UTF-8 comments made cmd hang at parse time.
REM ============================================================

setlocal EnableExtensions
set "SRC=%~dp0"
set "DESK=%USERPROFILE%\.dsh\profiles\desktop"
set "WEB=%USERPROFILE%\.dsh\profiles\web"

if not exist "%SRC%index.js" (
  echo [deploy] source missing: "%SRC%index.js"
  exit /b 1
)

call :deploy "%DESK%\dsh-ocdup-panel"
call :deploy "%DESK%\node_modules\dsh-ocdup-panel"
if /I "%~1"=="web" (
  call :deploy "%WEB%\dsh-ocdup-panel"
  call :deploy "%WEB%\node_modules\dsh-ocdup-panel"
)

echo [deploy] done - restart DeepSeek Harness to load the new build.
exit /b 0

:deploy
if not exist "%~1" mkdir "%~1"
for %%F in (index.js client.js package.json cordis.patch.yml README.md test.js) do (
  copy /Y "%SRC%%%F" "%~1\" >nul
)
echo [deploy] %~1
exit /b 0
