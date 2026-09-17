@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem Build and start LeafHotKey from the latest repository commit.
rem Set LEAFHOTKEY_DRYRUN=1 to print selected paths without starting anything.

set "ROOT=%~dp0"
set "REPO=%ROOT:~0,-1%"
set "RELEASE=%ROOT%src\LeafHotKey\bin\Release\net8.0-windows"
set "HOST_RELEASE=%RELEASE%\LeafHotKey.exe"
set "WATCHER_RELEASE=%ROOT%src\LeafHotKeyWatcher\bin\Release\net8.0-windows\LeafHotKeyWatcher.exe"
set "MARKER=%RELEASE%\.leafhotkey-commit"
set "HOST="
set "WATCHER="

for %%D in ("publish" "src\LeafHotKey\bin\Release\net8.0-windows" "src\LeafHotKey\bin\Debug\net8.0-windows") do (
    if not defined HOST if exist "%ROOT%%%~D\LeafHotKey.exe" set "HOST=%ROOT%%%~D\LeafHotKey.exe"
)

for %%D in ("publish" "src\LeafHotKeyWatcher\bin\Release\net8.0-windows" "src\LeafHotKeyWatcher\bin\Debug\net8.0-windows") do (
    if not defined WATCHER if exist "%ROOT%%%~D\LeafHotKeyWatcher.exe" set "WATCHER=%ROOT%%%~D\LeafHotKeyWatcher.exe"
)

if "%LEAFHOTKEY_DRYRUN%"=="1" (
    echo HOST=%HOST%
    echo WATCHER=%WATCHER%
    exit /b 0
)

rem Compare the repository commit before checking running processes.
set "HEAD="
for /f "delims=" %%H in ('git -C "%REPO%" rev-parse HEAD 2^>nul') do if not defined HEAD set "HEAD=%%H"
set "NEED_BUILD="
if defined HEAD (
    set "BUILT="
    if exist "%MARKER%" set /p "BUILT="<"%MARKER%"
    if not exist "%HOST_RELEASE%" set "NEED_BUILD=1"
    if not exist "%WATCHER_RELEASE%" set "NEED_BUILD=1"
    if /i not "!BUILT!"=="!HEAD!" set "NEED_BUILD=1"
    git -C "%REPO%" diff --quiet -- .
    if errorlevel 1 set "NEED_BUILD=1"
    git -C "%REPO%" diff --cached --quiet -- .
    if errorlevel 1 set "NEED_BUILD=1"
)
if not defined HEAD (
    echo Git repository was not found. Cannot determine the latest commit.
    exit /b 1
)

set "HOST_RUNNING="
if defined HOST (
    start /wait "" "%HOST%" --status >nul 2>nul
    if not errorlevel 1 set "HOST_RUNNING=1"
)

set "WATCHER_RUNNING="
powershell -NoProfile -Command "$root=[IO.Path]::GetFullPath('%ROOT%');$found=$false;foreach($item in (Get-Process -Name 'LeafHotKeyWatcher' -ErrorAction SilentlyContinue)){if($item.Path -and $item.Path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){$found=$true;break}};if($found){exit 0};exit 1" >nul 2>nul
if not errorlevel 1 set "WATCHER_RUNNING=1"

if defined NEED_BUILD (
    echo A newer commit requires a Release rebuild.
    if defined WATCHER_RUNNING (
        echo Stopping the LeafHotKeyWatcher process...
        powershell -NoProfile -Command "$root=[IO.Path]::GetFullPath('%ROOT%');foreach($item in (Get-Process -Name 'LeafHotKeyWatcher' -ErrorAction SilentlyContinue)){if($item.Path -and $item.Path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){Stop-Process -Id $item.Id -Force}}" >nul 2>nul
    )
    if defined HOST_RUNNING (
        echo Stopping the LeafHotKey process...
        start /wait "" "%HOST%" --shutdown >nul 2>nul
    )
    call :wait_for_stopped
    if errorlevel 1 exit /b 1
    goto build
)

if defined HOST_RUNNING (
    echo LeafHotKey is already running.
    exit /b 0
)

goto select

:build
echo Building Release from !HEAD!...
dotnet build "%ROOT%src\LeafHotKey\LeafHotKey.csproj" -c Release --nologo
if errorlevel 1 (
    echo LeafHotKey build failed.
    exit /b 1
)
dotnet build "%ROOT%src\LeafHotKeyWatcher\LeafHotKeyWatcher.csproj" -c Release --nologo
if errorlevel 1 (
    echo LeafHotKeyWatcher build failed.
    exit /b 1
)
>"%MARKER%" echo !HEAD!
set "HOST=%HOST_RELEASE%"
set "WATCHER=%WATCHER_RELEASE%"

:select
if defined HEAD if exist "%HOST_RELEASE%" set "HOST=%HOST_RELEASE%"
if defined HEAD if exist "%WATCHER_RELEASE%" set "WATCHER=%WATCHER_RELEASE%"

if not defined HOST (
    echo LeafHotKey.exe was not found. Run scripts\Publish.ps1 first.
    exit /b 1
)

start "" "%HOST%"

if defined WATCHER (
    powershell -NoProfile -Command "Start-Sleep -Seconds 2"
    start "" /min "%WATCHER%" --run
)

exit /b 0

:wait_for_stopped
set "WAIT_COUNT=0"
:wait_loop
set "STILL_RUNNING="
if defined HOST (
    start /wait "" "%HOST%" --status >nul 2>nul
    if not errorlevel 1 set "STILL_RUNNING=1"
)
powershell -NoProfile -Command "$root=[IO.Path]::GetFullPath('%ROOT%');$found=$false;foreach($item in (Get-Process -Name 'LeafHotKeyWatcher' -ErrorAction SilentlyContinue)){if($item.Path -and $item.Path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){$found=$true;break}};if($found){exit 0};exit 1" >nul 2>nul
if not errorlevel 1 set "STILL_RUNNING=1"
if not defined STILL_RUNNING exit /b 0
set /a WAIT_COUNT+=1
if !WAIT_COUNT! GEQ 30 (
    echo Timed out waiting for LeafHotKey processes to stop.
    exit /b 1
)
powershell -NoProfile -Command "Start-Sleep -Seconds 1"
goto wait_loop
