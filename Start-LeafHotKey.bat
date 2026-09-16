@echo off
setlocal

rem LeafHotKey を起動する。
rem 発行物（publish）があればそれを使い、無ければビルド出力を使う。
rem LEAFHOTKEY_DRYRUN=1 を指定すると、解決したパスを表示するだけで起動しない。

set "ROOT=%~dp0"
set "HOST="
set "WATCHER="

for %%D in ("publish" "src\LeafHotKey\bin\Release\net8.0-windows" "src\LeafHotKey\bin\Debug\net8.0-windows") do (
    if not defined HOST if exist "%ROOT%%%~D\LeafHotKey.exe" set "HOST=%ROOT%%%~D\LeafHotKey.exe"
)

for %%D in ("publish" "src\LeafHotKeyWatcher\bin\Release\net8.0-windows" "src\LeafHotKeyWatcher\bin\Debug\net8.0-windows") do (
    if not defined WATCHER if exist "%ROOT%%%~D\LeafHotKeyWatcher.exe" set "WATCHER=%ROOT%%%~D\LeafHotKeyWatcher.exe"
)

if not defined HOST (
    echo LeafHotKey.exe が見つかりません。先に scripts\Publish.ps1 を実行してください。
    exit /b 1
)

if "%LEAFHOTKEY_DRYRUN%"=="1" (
    echo HOST=%HOST%
    echo WATCHER=%WATCHER%
    exit /b 0
)

rem 既に動作している場合は二重に起動しない。
start /wait "" "%HOST%" --status
if "%ERRORLEVEL%"=="0" (
    echo LeafHotKey は既に動作しています。
    exit /b 0
)

start "" "%HOST%"

rem ゲーム保護の監視。危険なプロセスを検知したら本体を退避し、終了後に再起動する。
if defined WATCHER (
    timeout /t 2 /nobreak >nul
    start "" /min "%WATCHER%" --run
)

exit /b 0
