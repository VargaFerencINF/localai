<#
.SYNOPSIS
  End-to-end simulation (docs/DEV_MODE.md section 4), PowerShell twin of e2e-sim.sh: starts EfMailAgent with the mock
  LLM and runs the add-on core against it with a simulated Thunderbird. Exit code != 0 on any mismatch.
.PARAMETER Exe
  Path to EfMailAgent.exe (or EfMailAgent.dll, run with dotnet). Default: build Release and use the dll.
#>
[CmdletBinding()]
param(
    [string] $Exe,
    [string] $DataDir,
    [int] $Port = 47899
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $DataDir) { $DataDir = Join-Path $root '.e2edata' }

if (-not $Exe) {
    dotnet build (Join-Path $root 'src/EfMailAgent') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }
    $Exe = Join-Path $root 'src/EfMailAgent/bin/Release/net10.0/EfMailAgent.dll'
}
$Exe = (Resolve-Path $Exe).Path

if (Test-Path $DataDir) { Remove-Item $DataDir -Recurse -Force }
New-Item -ItemType Directory -Path $DataDir | Out-Null
$DataDir = (Resolve-Path $DataDir).Path

$bytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$settings = [ordered]@{
    Server = [ordered]@{ Port = $Port; Token = $token }
    Llm    = [ordered]@{ Provider = 'mock'; MockDir = (Join-Path $root 'samples/mock-responses'); MockDelayMs = 300 }
    Worker = [ordered]@{ IdlePollMilliseconds = 200 }
}
[System.IO.File]::WriteAllText((Join-Path $DataDir 'appsettings.json'), ($settings | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))

$log = Join-Path $DataDir 'efmailagent-console'
if ($Exe.ToLowerInvariant().EndsWith('.dll')) {
    $process = Start-Process dotnet -ArgumentList @("`"$Exe`"", '--data-dir', "`"$DataDir`"") -PassThru -NoNewWindow -RedirectStandardOutput "$log.out" -RedirectStandardError "$log.err"
} else {
    $process = Start-Process $Exe -ArgumentList @('--data-dir', "`"$DataDir`"") -PassThru -NoNewWindow -RedirectStandardOutput "$log.out" -RedirectStandardError "$log.err"
}

try {
    node (Join-Path $root 'scripts/simulate-thunderbird.mjs') --data-dir $DataDir
    if ($LASTEXITCODE -ne 0) {
        Write-Host '--- EfMailAgent napló ---'
        Get-Content "$log.err" -ErrorAction SilentlyContinue
        exit 1
    }
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}
