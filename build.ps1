<#
.SYNOPSIS
  Builds EfMailAgent: tests (C# + add-on), add-on XPI, single-file win-x64 exe, Inno Setup installer. Output in .\dist:
  EfMailAgent.exe, efmailagent.xpi, EfMailAgent-Setup.exe. The version comes from the VERSION file
  (change it with: node scripts/set-version.mjs 1.2.3).
.PARAMETER Sign
  Sign EfMailAgent.exe and the installer with signtool (optional; unsigned files may trigger SmartScreen / antivirus).
  Needs -CertificateThumbprint (certificate in the current user's or the machine's store) and signtool on PATH or in a
  Windows SDK folder.
.EXAMPLE
  .\build.ps1
  .\build.ps1 -SkipTests -SkipInstaller
  .\build.ps1 -Sign -CertificateThumbprint 0123456789ABCDEF...
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $SkipTests,
    [switch] $AddonOnly,
    [switch] $SkipInstaller,
    [switch] $Sign,
    [string] $CertificateThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()

function Find-Tool([string] $name, [string[]] $candidates) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in $candidates) {
        $found = Get-ChildItem -Path $candidate -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

function Invoke-Sign([string] $file) {
    if (-not $Sign) { return }
    if (-not $CertificateThumbprint) { throw '-Sign requires -CertificateThumbprint' }
    $signtool = Find-Tool 'signtool.exe' @("${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe")
    if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK)' }
    Write-Step "Sign $([IO.Path]::GetFileName($file))"
    Invoke-Checked 'signtool' { & $signtool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $file }
}

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
    Invoke-Sign (Join-Path $dist 'EfMailAgent.exe')

    if (-not $SkipInstaller) {
        Write-Step 'Installer (Inno Setup)'
        $iscc = Find-Tool 'iscc.exe' @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
        if (-not $iscc) { throw 'Inno Setup 6 (iscc.exe) not found. Install it or use -SkipInstaller.' }
        Invoke-Checked 'iscc' { & $iscc /Q "/DAppVersion=$version" "/DDistDir=$dist" "/DRepoDir=$root" (Join-Path $root 'installer\EfMailAgent.iss') }
        Invoke-Sign (Join-Path $dist 'EfMailAgent-Setup.exe')
    }
}

Write-Step 'Done'
Get-ChildItem $dist | ForEach-Object { Write-Host ("  {0,-24} {1,12:N0} bytes" -f $_.Name, $_.Length) }
