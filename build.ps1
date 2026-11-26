<#
.SYNOPSIS
  Builds EfMailAgent: tests (C# + add-on), add-on XPI, single-file win-x64 exe. Output in .\dist.
  Step 7 adds the Inno Setup installer to this script.
.EXAMPLE
  .\build.ps1
  .\build.ps1 -SkipTests
  .\build.ps1 -AddonOnly
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $SkipTests,
    [switch] $AddonOnly
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()

function Write-Step([string] $text) { Write-Host "`n=== $text" -ForegroundColor Cyan }
function Invoke-Checked([string] $what, [scriptblock] $command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

Write-Step "EfMailAgent $version ($Configuration)"
$manifest = Get-Content (Join-Path $root 'addon/manifest.json') -Raw | ConvertFrom-Json
if ($manifest.version -ne $version) { throw "addon/manifest.json version ($($manifest.version)) differs from VERSION ($version)" }

Write-Step 'Clean'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

if (-not $SkipTests) {
    if (-not $AddonOnly) {
        Write-Step 'C# tests'
        Invoke-Checked 'dotnet test' { dotnet test (Join-Path $root 'EfMailAgent.sln') -c $Configuration --nologo }
    }
    Write-Step 'Add-on tests (Node)'
    Push-Location (Join-Path $root 'addon')
    try { Invoke-Checked 'node --test' { node --test 'test/*.test.js' } } finally { Pop-Location }
}

Write-Step 'Add-on package (XPI)'
Invoke-Checked 'package-addon' { node (Join-Path $root 'scripts/package-addon.mjs') --out (Join-Path $dist 'efmailagent.xpi') }

if (-not $AddonOnly) {
    Write-Step 'Publish single-file exe (win-x64, self-contained)'
    $publish = Join-Path $dist 'publish'
    Invoke-Checked 'dotnet publish' {
        dotnet publish (Join-Path $root 'src/EfMailAgent') -c $Configuration -r win-x64 --self-contained `
            -p:PublishSingleFile=true -o $publish --nologo
    }
    # The exe and the bundled XPI ship side by side (install-addon reads the XPI next to the exe, Step 5).
    Copy-Item (Join-Path $publish 'EfMailAgent.exe') $dist
    Remove-Item $publish -Recurse -Force
}

Write-Step 'Done'
Get-ChildItem $dist | ForEach-Object { Write-Host ("  {0,-24} {1,12:N0} bytes" -f $_.Name, $_.Length) }
