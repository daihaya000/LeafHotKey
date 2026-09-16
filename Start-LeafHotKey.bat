@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem LeafHotKey を起動する。
rem リポジトリの最新コミットと Release ビルドの記録を比較し、古ければ自動ビルドする。
rem LEAFHOTKEY_DRYRUN=1 を指定すると、解決したパスを表示するだけで起動しない。

set "ROOT=%~dp0"
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

rem 稼働中のファイルはビルドできないため、先に起動済みか確認する。
set "HOST_RUNNING="
if defined HOST (
    start /wait "" "%HOST%" --status >nul 2>nul
    if not errorlevel 1 set "HOST_RUNNING=1"
)
if defined HOST_RUNNING (
    echo LeafHotKey は既に動作しています。
    exit /b 0
)

tasklist /FI "IMAGENAME eq LeafHotKeyWatcher.exe" /NH 2>nul | find /I "LeafHotKeyWatcher.exe" >nul
if not errorlevel 1 goto select

rem Git が使えるリポジトリでは、Release 出力が最新コミットか確認する。
set "HEAD="
for /f "delims=" %%H in ('git -C "%ROOT%" rev-parse HEAD 2^>nul') do if not defined HEAD set "HEAD=%%H"
set "NEED_BUILD="
if defined HEAD (
    set "BUILT="
    if exist "%MARKER%" set /p "BUILT="<"%MARKER%"
    if not exist "%HOST_RELEASE%" set "NEED_BUILD=1"
    if not exist "%WATCHER_RELEASE%" set "NEED_BUILD=1"
    if /i not "!BUILT!"=="!HEAD!" set "NEED_BUILD=1"
    git -C "%ROOT%" diff --quiet -- .
    if errorlevel 1 set "NEED_BUILD=1"
    git -C "%ROOT%" diff --cached --quiet -- .
    if errorlevel 1 set "NEED_BUILD=1"
)

if defined NEED_BUILD goto build

goto select

:build
echo 最新コミットに合わせて Release ビルドを実行します...
dotnet build "%ROOT%src\LeafHotKey\LeafHotKey.csproj" -c Release --nologo
if errorlevel 1 (
    echo LeafHotKey 本体のビルドに失敗しました。
    exit /b 1
)
dotnet build "%ROOT%src\LeafHotKeyWatcher\LeafHotKeyWatcher.csproj" -c Release --nologo
if errorlevel 1 (
    echo LeafHotKeyWatcher のビルドに失敗しました。
    exit /b 1
)
>"%MARKER%" echo !HEAD!
set "HOST=%HOST_RELEASE%"
set "WATCHER=%WATCHER_RELEASE%"

:select
if defined HEAD if exist "%HOST_RELEASE%" set "HOST=%HOST_RELEASE%"
if defined HEAD if exist "%WATCHER_RELEASE%" set "WATCHER=%WATCHER_RELEASE%"

if not defined HOST (
    echo LeafHotKey.exe が見つかりません。先に scripts\Publish.ps1 を実行してください。
    exit /b 1
)

start "" "%HOST%"

rem ゲーム保護の監視。危険なプロセスを検知したら本体を退避し、終了後に再起動する。
if defined WATCHER (
    timeout /t 2 /nobreak >nul
    start "" /min "%WATCHER%" --run
)

exit /b 0
