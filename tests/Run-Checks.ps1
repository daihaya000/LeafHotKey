<#
.SYNOPSIS
LeafHotKey の自動チェックをまとめて実行する。

.DESCRIPTION
本体（WebUI・ゲーム保護監視）と入力エンジン（LeafHotKeyEngine）をビルドし、すべての自己検証を順に実行する。
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
$engineProject = Join-Path $repositoryRoot 'src\LeafHotKeyEngine\LeafHotKeyEngine.csproj'
$settings = Join-Path $repositoryRoot 'defaults\settings.json'
$hostExe = Join-Path $repositoryRoot "src\LeafHotKey\bin\$Configuration\net8.0-windows\LeafHotKey.exe"
$engineExe = Join-Path $repositoryRoot "src\LeafHotKeyEngine\bin\$Configuration\net8.0-windows\LeafHotKeyEngine.exe"

function Invoke-Build {
    param([string] $Project)

    Write-Host "build: $Project"
    & dotnet build $Project -c $Configuration --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "ビルドに失敗しました: $Project"
    }
}

# 本体もエンジンも GUI 実行ファイルでコンソールへ出力できないため、結果はレポートファイルで受け取る。
function Invoke-GuiCheck {
    param([string] $Exe, [string] $Mode, [switch] $WithSettings)

    $report = Join-Path ([System.IO.Path]::GetTempPath()) ("leafhotkey-runchecks-" + [guid]::NewGuid().ToString('N') + ".txt")
    $arguments = @($Mode, $report)
    if ($WithSettings) { $arguments += $settings }

    try {
        $process = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
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

Invoke-Build -Project $hostProject
Invoke-Build -Project $engineProject

$results = @()

# 入力エンジン（フック・送信・AHK）。
$results += Invoke-GuiCheck -Exe $engineExe -Mode '--check'
$results += Invoke-GuiCheck -Exe $engineExe -Mode '--check-hook'
$results += Invoke-GuiCheck -Exe $engineExe -Mode '--check-engine' -WithSettings
$results += Invoke-GuiCheck -Exe $engineExe -Mode '--check-send'
$results += Invoke-GuiCheck -Exe $engineExe -Mode '--check-backend'
$results += Invoke-GuiCheck -Exe $engineExe -Mode '--check-coverage' -WithSettings

# 本体（設定画面・ゲーム保護監視）。
$results += Invoke-GuiCheck -Exe $hostExe -Mode '--check-profiles' -WithSettings
$results += Invoke-GuiCheck -Exe $hostExe -Mode '--check-settings' -WithSettings
$results += Invoke-GuiCheck -Exe $hostExe -Mode '--check-server' -WithSettings
$results += Invoke-GuiCheck -Exe $hostExe -Mode '--check-watch' -WithSettings

$results | Format-Table -AutoSize | Out-String -Width 120 | Write-Host

$failed = @($results | Where-Object { $_.ExitCode -ne 0 -or $_.Fail -gt 0 })
$totalPass = ($results | Measure-Object -Property Pass -Sum).Sum

if ($failed.Count -gt 0) {
    Write-Host "失敗: $($failed.Count) 件 / 合格: $totalPass 件"
    exit 1
}

Write-Host "すべて合格しました（合格 $totalPass 件）。"
exit 0
