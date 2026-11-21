# ToDooo release helper.
# Keeps version numbers in one place (csproj + app.manifest), builds, tests and packs a ZIP.
# ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less scripts as ANSI.
#
# Usage:
#   pwsh scripts/release.ps1 -Version 1.4.0
#   pwsh scripts/release.ps1 -Version 1.4.1 -SkipTests
#   pwsh scripts/release.ps1 -Version 1.5.0 -Deploy

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$SkipTests,
    [switch]$Deploy,
    [string]$DeployDir = 'C:\apps\ToDooo'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'src/ToDooo.App/ToDooo.csproj'
$manifest = Join-Path $root 'src/ToDooo.App/app.manifest'
$tests = Join-Path $root 'tests/ToDooo.Tests/ToDooo.Tests.csproj'
$publishDir = Join-Path $root 'src/ToDooo.App/bin/Release/net11.0-windows10.0.26100.0/win-x64/publish'
$zipDir = Join-Path $root 'publish'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be MAJOR.MINOR.PATCH (e.g. 1.4.0). Got: '$Version'"
}

function Update-CsprojVersion {
    param([string]$Path, [string]$Version)
    $text = Get-Content $Path -Raw
    $text = [regex]::Replace($text, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
    $text = [regex]::Replace($text, '<AssemblyVersion>[^<]*</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>")
    $text = [regex]::Replace($text, '<FileVersion>[^<]*</FileVersion>', "<FileVersion>$Version.0</FileVersion>")
    $text = [regex]::Replace($text, '<InformationalVersion>[^<]*</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>")
    Set-Content -Path $Path -Value $text -NoNewline -Encoding UTF8
}

function Update-ManifestVersion {
    param([string]$Path, [string]$Version)
    $text = Get-Content $Path -Raw
    $text = [regex]::Replace($text, 'assemblyIdentity version="[^"]*"', 'assemblyIdentity version="' + $Version + '.0"')
    Set-Content -Path $Path -Value $text -NoNewline -Encoding UTF8
}

Write-Host "[1/4] Version -> $Version"
Update-CsprojVersion -Path $csproj -Version $Version
Update-ManifestVersion -Path $manifest -Version $Version

if ($SkipTests) {
    Write-Host '[2/4] Tests skipped'
} else {
    Write-Host '[2/4] Running tests'
    dotnet test $tests
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }
}

Write-Host '[3/4] Publishing self-contained Release (win-x64)'
dotnet publish $csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }

Write-Host '[4/4] Packing ZIP'
New-Item -ItemType Directory -Path $zipDir -Force | Out-Null
$zip = Join-Path $zipDir "ToDooo-$Version-win-x64.zip"
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ('todooo-stage-' + [guid]::NewGuid().ToString('N'))
$stageApp = Join-Path $stage 'ToDooo'
New-Item -ItemType Directory -Path $stageApp -Force | Out-Null
Copy-Item -Path (Join-Path $publishDir '*') -Destination $stageApp -Recurse -Force
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $stage -Recurse -Force

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content -Path ($zip + '.sha256') -Value $hash -Encoding ASCII

Write-Host ''
Write-Host "ZIP  : $zip"
Write-Host ("SIZE : {0:N1} MB" -f ((Get-Item $zip).Length / 1MB))
Write-Host "SHA  : $hash"

if ($Deploy) {
    if (-not (Test-Path $DeployDir)) { throw "Deploy dir not found: $DeployDir" }
    Write-Host "[deploy] Replacing $DeployDir"
    $running = Get-Process ToDooo -ErrorAction SilentlyContinue
    if ($running) {
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
    robocopy $publishDir $DeployDir /MIR /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
    Start-Process (Join-Path $DeployDir 'ToDooo.exe')
    Write-Host '[deploy] Done'
}

Write-Host ''
Write-Host 'Next steps:'
Write-Host "  git add -A"
Write-Host "  git commit -m ""release: v$Version"""
Write-Host "  git tag v$Version"
Write-Host '  git push github main --tags'