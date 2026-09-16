<#
.SYNOPSIS
LeafHotKey の自動チェックをまとめて実行する。

.DESCRIPTION
本体と Watcher をビルドし、すべての自己検証を順に実行する。
1 件でも失敗したら終了コード 1 を返す。
検証用のキー送信はフックで破棄されるため、他のアプリへは入力されない。

.PARAMETER Configuration
ビルド構成。既定は Debug。

.EXAMPLE
powershell -NoProfile -File tests\Run-Checks.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$hostProject = Join-Path $repositoryRoot 'src\LeafHotKey\LeafHotKey.csproj'
$watcherProject = Join-Path $repositoryRoot 'src\LeafHotKeyWatcher\LeafHotKeyWatcher.csproj'
$settings = Join-Path $repositoryRoot 'defaults\settings.json'
$hostExe = Join-Path $repositoryRoot "src\LeafHotKey\bin\$Configuration\net8.0-windows\LeafHotKey.exe"
$watcherExe = Join-Path $repositoryRoot "src\LeafHotKeyWatcher\bin\$Configuration\net8.0-windows\LeafHotKeyWatcher.exe"

function Invoke-Build {
    param([string] $Project)

    Write-Host "build: $Project"
    & dotnet build $Project -c $Configuration --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "ビルドに失敗しました: $Project"
    }
}

# 本体は GUI 実行ファイルでコンソールへ出力できないため、結果はレポートファイルで受け取る。
function Invoke-HostCheck {
    param([string] $Mode, [switch] $WithSettings)

    $report = Join-Path ([System.IO.Path]::GetTempPath()) ("leafhotkey-runchecks-" + [guid]::NewGuid().ToString('N') + ".txt")
    $arguments = @($Mode, $report)
    if ($WithSettings) { $arguments += $settings }

    try {
        $process = Start-Process -FilePath $hostExe -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(60000)) {
            $process.Kill()
            return [pscustomobject]@{ Name = $Mode; Pass = 0; Fail = 0; ExitCode = -1; Note = 'タイムアウト' }
        }

        $lines = if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Encoding UTF8 } else { @() }
        $checked = ($lines | Where-Object { $_ -cmatch '^checked=' }) -join ''

        return [pscustomobject]@{
            Name     = $Mode
            Pass     = ($lines | Where-Object { $_ -cmatch '^PASS ' }).Count
            Fail     = ($lines | Where-Object { $_ -cmatch '^FAIL ' }).Count
            ExitCode = $process.ExitCode
            Note     = $checked
        }
    }
    finally {
        Remove-Item -LiteralPath $report -Force -ErrorAction SilentlyContinue
    }
}

# Watcher はコンソールアプリなので標準出力をそのまま数える。
function Invoke-WatcherCheck {
    param([string] $Mode, [switch] $WithSettings)

    $arguments = @($Mode)
    if ($WithSettings) { $arguments += $settings }

    # ネイティブコマンドの標準エラー出力を終了エラーにしない（想定内の警告を含むため）。
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $watcherExe @arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    return [pscustomobject]@{
        Name     = "watcher $Mode"
        Pass     = ($output | Where-Object { $_ -cmatch '^PASS ' }).Count
        Fail     = ($output | Where-Object { $_ -cmatch '^FAIL ' }).Count
        ExitCode = $exitCode
        Note     = ''
    }
}

Invoke-Build -Project $hostProject
Invoke-Build -Project $watcherProject

$results = @()
$results += Invoke-HostCheck -Mode '--check'
$results += Invoke-HostCheck -Mode '--check-profiles' -WithSettings
$results += Invoke-HostCheck -Mode '--check-send'
$results += Invoke-HostCheck -Mode '--check-engine' -WithSettings
$results += Invoke-HostCheck -Mode '--check-hook'
$results += Invoke-HostCheck -Mode '--check-coverage' -WithSettings
$results += Invoke-HostCheck -Mode '--check-settings' -WithSettings
$results += Invoke-HostCheck -Mode '--check-server' -WithSettings
$results += Invoke-WatcherCheck -Mode '--check'
$results += Invoke-WatcherCheck -Mode '--check-lifecycle' -WithSettings

$results | Format-Table -AutoSize | Out-String -Width 120 | Write-Host

$failed = @($results | Where-Object { $_.ExitCode -ne 0 -or $_.Fail -gt 0 })
$totalPass = ($results | Measure-Object -Property Pass -Sum).Sum

if ($failed.Count -gt 0) {
    Write-Host "失敗: $($failed.Count) 件 / 合格: $totalPass 件"
    exit 1
}

Write-Host "すべて合格しました（合格 $totalPass 件）。"
exit 0
