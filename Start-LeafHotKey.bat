@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem Build and start LeafHotKey from the latest repository commit.
rem The host (LeafHotKey.exe) starts and supervises LeafHotKeyEngine.exe, so only the host is launched here.
rem Set LEAFHOTKEY_DRYRUN=1 to print selected paths without starting anything.
rem Called with "restart" from the tray restart: stop the old process first, then rebuild and start.
rem Runs are serialized with a lock directory so two sessions cannot build and start at the same time.

set "ROOT=%~dp0"
set "REPO=%ROOT:~0,-1%"
set "RELEASE=%ROOT%src\LeafHotKey\bin\Release\net8.0-windows"
set "HOST_RELEASE=%RELEASE%\LeafHotKey.exe"
set "ENGINE_RELEASE=%ROOT%src\LeafHotKeyEngine\bin\Release\net8.0-windows\LeafHotKeyEngine.exe"
set "MARKER=%RELEASE%\.leafhotkey-commit"
set "LOG=%LOCALAPPDATA%\LeafHotKey\restart.log"
if not defined LOCALAPPDATA set "LOG=%TEMP%\leafhotkey-restart.log"
set "LOCK=%LOG%.lock"
set "MODE=%~1"
set "HOST="
set "RESULT=1"

rem Write the decisions to a log file so a failed restart can be traced (reset when it grows).
if defined LOCALAPPDATA if not exist "%LOCALAPPDATA%\LeafHotKey" md "%LOCALAPPDATA%\LeafHotKey" >nul 2>nul
if exist "%LOG%" for %%F in ("%LOG%") do if %%~zF GTR 65536 del "%LOG%" >nul 2>nul

for %%D in ("src\LeafHotKey\bin\Release\net8.0-windows" "src\LeafHotKey\bin\Debug\net8.0-windows" "publish") do (
    if not defined HOST if exist "%ROOT%%%~D\LeafHotKey.exe" set "HOST=%ROOT%%%~D\LeafHotKey.exe"
)

if "%LEAFHOTKEY_DRYRUN%"=="1" (
    echo HOST=%HOST%
    echo ENGINE=%ENGINE_RELEASE%
    exit /b 0
)

call :acquire_lock
if errorlevel 1 exit /b 0
call :log "start mode=[%MODE%]"

rem Compare the repository commit before checking running processes.
set "HEAD="
for /f "delims=" %%H in ('git -C "%REPO%" rev-parse HEAD 2^>nul') do if not defined HEAD set "HEAD=%%H"
set "NEED_BUILD="
if defined HEAD (
    set "BUILT="
    if exist "%MARKER%" set /p "BUILT="<"%MARKER%"
    if not exist "%HOST_RELEASE%" set "NEED_BUILD=1"
    if not exist "%ENGINE_RELEASE%" set "NEED_BUILD=1"
    if /i not "!BUILT!"=="!HEAD!" set "NEED_BUILD=1"
    git -C "%REPO%" diff --quiet -- .
    if errorlevel 1 set "NEED_BUILD=1"
    git -C "%REPO%" diff --cached --quiet -- .
    if errorlevel 1 set "NEED_BUILD=1"
)
if not defined HEAD (
    rem Without git, the commit check is skipped and the existing build is started.
    echo Git was not found. Starting the existing build without the commit check.
    call :log "git not found"
)
call :log "head=[%HEAD%] built=[%BUILT%] need_build=[%NEED_BUILD%]"

rem The tray restart runs while the old process is still exiting: wait for it before probing.
if /i "%MODE%"=="restart" (
    call :stop_host
    if errorlevel 1 (
        call :log "restart failed: the old process did not stop"
        echo Timed out waiting for LeafHotKey to stop.
        goto finish
    )
)

set "HOST_RUNNING="
if defined HOST (
    start /wait "" "%HOST%" --status >nul 2>nul
    if not errorlevel 1 set "HOST_RUNNING=1"
)
call :log "host=[%HOST%] host_running=[%HOST_RUNNING%]"

if defined NEED_BUILD (
    echo A newer commit requires a Release rebuild.
    call :stop_host
    if errorlevel 1 (
        call :log "rebuild failed: the old process did not stop"
        goto finish
    )
    rem The host stops its engine on exit. Stop any leftover engine so the rebuild can replace the exe.
    powershell -NoProfile -Command "$root=[IO.Path]::GetFullPath('%ROOT%');foreach($item in (Get-Process -Name 'LeafHotKeyEngine' -ErrorAction SilentlyContinue)){if($item.Path -and $item.Path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){Stop-Process -Id $item.Id -Force}}" >nul 2>nul
    call :wait_for_stopped
    if errorlevel 1 goto finish
    goto build
)

if defined HOST_RUNNING (
    echo LeafHotKey is already running.
    call :log "already running"
    set "RESULT=0"
    goto finish
)

goto select

:build
echo Building Release from !HEAD!...
call :log "building from !HEAD!"
dotnet build "%ROOT%src\LeafHotKey\LeafHotKey.csproj" -c Release --nologo
if errorlevel 1 goto build_failed
dotnet build "%ROOT%src\LeafHotKeyEngine\LeafHotKeyEngine.csproj" -c Release --nologo
if errorlevel 1 goto build_failed
>"%MARKER%" echo !HEAD!
set "HOST=%HOST_RELEASE%"
call :log "build done"
goto select

:build_failed
rem Start the existing build when the rebuild fails, so a restart never leaves the app gone.
echo Build failed. Starting the existing build.
call :log "build failed; starting the existing build"
if not exist "%HOST_RELEASE%" goto finish
set "HOST=%HOST_RELEASE%"
goto select

:select
if defined HEAD if exist "%HOST_RELEASE%" set "HOST=%HOST_RELEASE%"

if not defined HOST (
    echo LeafHotKey.exe was not found. Run scripts\Publish.ps1 first.
    call :log "host exe not found"
    goto finish
)

rem The host starts the input engine itself (LeafHotKeyEngine.exe) and keeps the WebUI alive.
rem Tell the app that it came back from a restart so it can say so in the tray.
if /i "%MODE%"=="restart" set "LEAFHOTKEY_RESTARTED=1"
start "" "%HOST%"
call :log "started [%HOST%]"
set "RESULT=0"

:finish
rd /s /q "%LOCK%" >nul 2>nul
exit /b %RESULT%

:acquire_lock
rem Serialize runs: only one build/start at a time. A lock left by a dead run is removed after 3 minutes.
set "LOCK_WAIT=0"
:lock_loop
mkdir "%LOCK%" 2>nul
if not errorlevel 1 exit /b 0
set /a LOCK_WAIT+=1
if !LOCK_WAIT! GEQ 20 (
    call :log "another run holds the lock; nothing started"
    exit /b 1
)
if !LOCK_WAIT! GEQ 6 (
    for /f %%A in ('powershell -NoProfile -Command "if ((Get-Date) - (Get-Item -LiteralPath '%LOCK%' -ErrorAction SilentlyContinue).LastWriteTime -gt (New-TimeSpan -Minutes 3)) { 'stale' }" 2^>nul') do set "STALE=%%A"
    if defined STALE (
        call :log "removing a lock left by an older run"
        set "STALE="
        rd /s /q "%LOCK%" >nul 2>nul
    )
)
ping 127.0.0.1 -n 2 >nul
goto lock_loop

:stop_host
rem Ask a running host to stop and wait for it; do nothing when it is already gone.
if not defined HOST exit /b 0
start /wait "" "%HOST%" --status >nul 2>nul
if errorlevel 1 exit /b 0
call :log "stopping the host"
start /wait "" "%HOST%" --shutdown >nul 2>nul
call :wait_for_stopped
exit /b %errorlevel%

:wait_for_stopped
set "WAIT_COUNT=0"
:wait_loop
set "STILL_RUNNING="
if defined HOST (
    start /wait "" "%HOST%" --status >nul 2>nul
    if not errorlevel 1 set "STILL_RUNNING=1"
)
if not defined STILL_RUNNING exit /b 0
set /a WAIT_COUNT+=1
if !WAIT_COUNT! GEQ 30 (
    call :log "timed out waiting for the host to stop"
    exit /b 1
)
goto wait_loop

:log
>>"%LOG%" echo [%date% %time%] %~1
exit /b 0
