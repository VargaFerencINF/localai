<#
.SYNOPSIS
  Posts a sample email to the running EfMailAgent API and waits for the job to finish (development helper).
.EXAMPLE
  .\scripts\dev-submit.ps1
  .\scripts\dev-submit.ps1 -File samples\emails\elorendeles_03_tablazat.txt -NewMessageId
#>
[CmdletBinding()]
param(
    [string] $File = 'samples\emails\demo_eloreendeles_01.eml',
    [string] $DataDir = '.devdata',
    [string] $Url,
    [string] $Token,
    [string] $Subject,
    [string] $From,
    [string] $Rule,
    [switch] $NewMessageId,
    [switch] $NoWait,
    [int] $TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$utf8 = New-Object System.Text.UTF8Encoding($false)

$settingsPath = Join-Path $DataDir 'appsettings.json'
$settings = $null
if (Test-Path $settingsPath) { $settings = [System.IO.File]::ReadAllText((Resolve-Path $settingsPath), $utf8) | ConvertFrom-Json }
if (-not $Url) {
    $port = 47811
    if ($settings -and $settings.Server -and $settings.Server.Port) { $port = $settings.Server.Port }
    $Url = "http://127.0.0.1:$port"
}
$Url = $Url.TrimEnd('/')
if (-not $Token -and $settings -and $settings.Server) { $Token = $settings.Server.Token }
if (-not $Token) { throw "Nincs token: add meg a -Token paraméterrel, vagy indítsd el egyszer az EfMailAgent-et ($settingsPath)." }

$path = (Resolve-Path $File).Path
$text = [System.IO.File]::ReadAllText($path, $utf8).Replace("`r`n", "`n")
$email = @{ body = $text }
if ($path.ToLowerInvariant().EndsWith('.eml')) {
    # Minimal parser for the plain-text sample .eml files (no MIME decoding).
    $split = $text.IndexOf("`n`n")
    $headerText = if ($split -lt 0) { $text } else { $text.Substring(0, $split) }
    $headerText = [regex]::Replace($headerText, "`n[ `t]+", ' ')
    foreach ($line in $headerText.Split("`n")) {
        $i = $line.IndexOf(':')
        if ($i -gt 0) { $email[$line.Substring(0, $i).Trim().ToLowerInvariant()] = $line.Substring($i + 1).Trim() }
    }
    $email.body = if ($split -lt 0) { '' } else { $text.Substring($split + 2) }
} else {
    $indexPath = Join-Path (Split-Path $path) 'index.json'
    if (Test-Path $indexPath) {
        $entry = ([System.IO.File]::ReadAllText($indexPath, $utf8) | ConvertFrom-Json).emails | Where-Object { $_.file -eq (Split-Path $path -Leaf) }
        if ($entry) { $email.subject = $entry.subject; $email.from = $entry.from; $email.date = $entry.receivedAt }
    }
}

$messageId = $email['message-id']
if ($NewMessageId -or -not $messageId) { $messageId = "<dev-submit-$([guid]::NewGuid().ToString('N'))@efmailagent.local>" }
# The weekday of an RFC 5322 date is ignored (the DEV_MODE demo fixture says 'Tue' for a Wednesday; .NET would reject it).
$receivedAt = if ($email.date) {
    $dateText = ([string]$email.date) -replace '^\s*[A-Za-z]{3},\s*', ''
    ([DateTimeOffset]::Parse($dateText, [Globalization.CultureInfo]::InvariantCulture)).ToUniversalTime().ToString('o')
} else { [DateTimeOffset]::UtcNow.ToString('o') }

$payload = [ordered]@{
    messageId       = $messageId
    account         = 'dev-submit'
    folder          = 'Inbox'
    receivedAt      = $receivedAt
    from            = $(if ($From) { $From } elseif ($email.from) { $email.from } else { 'Fejleszto <dev@efmailagent.local>' })
    to              = $(if ($email.to) { $email.to } else { 'eloreendeles@efmailagent.local' })
    subject         = $(if ($Subject) { $Subject } else { [string]$email.subject })
    bodyText        = $email.body
    attachmentNames = @()
    source          = $(if ($Rule) { 'manual' } else { 'auto' })
}
if ($Rule) { $payload.ruleId = $Rule }

$client = New-Object System.Net.Http.HttpClient
$client.DefaultRequestHeaders.Add('X-EfMailAgent-Token', $Token)
$json = $payload | ConvertTo-Json -Depth 5
$content = New-Object System.Net.Http.StringContent($json, $utf8, 'application/json')
try {
    $response = $client.PostAsync("$Url/api/jobs", $content).GetAwaiter().GetResult()
} catch {
    throw "Az API nem érhető el ($Url): $($_.Exception.InnerException.Message). Fut az EfMailAgent?"
}
$responseText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
Write-Host "POST /api/jobs -> HTTP $([int]$response.StatusCode) $responseText"
if ([int]$response.StatusCode -ne 202 -and [int]$response.StatusCode -ne 200) { exit 1 }
if ($NoWait) { exit 0 }

$jobId = ($responseText | ConvertFrom-Json).jobId
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ($true) {
    $job = $client.GetStringAsync("$Url/api/jobs/$jobId").GetAwaiter().GetResult() | ConvertFrom-Json
    if (@('done', 'needs_review', 'failed') -contains $job.status) {
        Write-Host "Feladat $($job.id): $($job.status), sorok: $($job.rowCount), kísérletek: $($job.attempts)"
        if ($job.outputFile) { Write-Host "CSV: $($job.outputFile)" }
        if ($job.error) { Write-Host "Megjegyzés / hiba: $($job.error)" }
        if ($job.status -eq 'failed') { exit 1 } else { exit 0 }
    }
    if ((Get-Date) -gt $deadline) { throw "A feladat $TimeoutSeconds mp alatt sem fejeződött be (állapot: $($job.status))." }
    Start-Sleep -Seconds 1
}
