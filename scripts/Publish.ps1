<#
.SYNOPSIS
LeafHotKey を Windows x64 向けに自己完結発行する。

.DESCRIPTION
本体（WebUI とゲーム保護監視）と入力エンジン（LeafHotKeyEngine）を同じフォルダーへ発行し、既定設定を添える。
.NET ランタイムを同梱するため、AutoHotkey も .NET も入っていない環境でそのまま実行できる。

.PARAMETER OutputDirectory
発行先。既定はリポジトリ直下の publish。

.PARAMETER Configuration
ビルド構成。既定は Release。

.EXAMPLE
powershell -NoProfile -File scripts\Publish.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot 'publish'
}

$hostProject = Join-Path $repositoryRoot 'src\LeafHotKey\LeafHotKey.csproj'
$engineProject = Join-Path $repositoryRoot 'src\LeafHotKeyEngine\LeafHotKeyEngine.csproj'
$settingsSource = Join-Path $repositoryRoot 'defaults\settings.json'

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Invoke-Publish {
    param([string] $Project)

    Write-Host "publish: $Project"
    & dotnet publish $Project `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -o $OutputDirectory `
        --nologo | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "発行に失敗しました: $Project"
    }
}

Invoke-Publish -Project $hostProject
Invoke-Publish -Project $engineProject

# 既定設定は発行物の中から探せる位置に置く（初回起動時のひな形になる）。
$defaultsDirectory = Join-Path $OutputDirectory 'defaults'
New-Item -ItemType Directory -Path $defaultsDirectory -Force | Out-Null
Copy-Item -LiteralPath $settingsSource -Destination (Join-Path $defaultsDirectory 'settings.json') -Force

$expected = @(
    'LeafHotKey.exe',
    'LeafHotKeyEngine.exe',
    'wwwroot\index.html',
    'wwwroot\styles.css',
    'wwwroot\app.js',
    'defaults\settings.json'
)

$missing = @($expected | Where-Object { -not (Test-Path -LiteralPath (Join-Path $OutputDirectory $_)) })
if ($missing.Count -gt 0) {
    throw "発行物が不足しています: $($missing -join ', ')"
}

# 自己完結かどうかはランタイム本体の有無で判断する。
if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'System.Private.CoreLib.dll'))) {
    throw '自己完結発行になっていません（ランタイムが同梱されていません）。'
}

$size = [math]::Round((Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host "発行しました: $OutputDirectory （$size MB）"
exit 0
