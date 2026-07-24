$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $scriptRoot 'MasterApp.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    throw 'MasterApp.exe was not found next to this setup script.'
}

$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$stateDir = Join-Path $localAppData 'MasterApp\State'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null

$driveRoot = Get-PSDrive -PSProvider FileSystem |
    ForEach-Object { Join-Path $_.Root 'My Drive\MasterApp' } |
    Where-Object { Test-Path -LiteralPath (Split-Path -Parent $_) } |
    Select-Object -First 1

if (-not $driveRoot) {
    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    $driveRoot = Join-Path $documents 'MasterApp'
}

$incoming = Join-Path $driveRoot 'Incoming'
$processed = Join-Path $driveRoot 'Processed'
$failed = Join-Path $driveRoot 'Failed'
$published = Join-Path $driveRoot 'Published'
@($driveRoot, $incoming, $processed, $failed, $published) | ForEach-Object {
    New-Item -ItemType Directory -Force -Path $_ | Out-Null
}

$settings = [ordered]@{
    runtimeMode = 'lazy-local'
    incomingFolder = $incoming
    processedFolder = $processed
    failedFolder = $failed
    publishedFolder = $published
    autoStartTunnel = $false
    logLevel = 'Info'
    packageScanIntervalSeconds = 5
    preferredLocalPort = 19057
    localPortFallbackCount = 20
    wifiOnly = $true
    sessionQrTtlSeconds = 60
    lifeJournal = [ordered]@{
        maxImagesForAnalysis = 16
        analysisTimeoutSeconds = 240
        autoFinalizeHourLocal = 3
        photoMaxWidth = 900
        jpegQuality = 75
    }
}

$settingsPath = Join-Path $stateDir 'settings.json'
$settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settingsPath -Encoding UTF8

$secretsPath = Join-Path $stateDir 'secrets.json'
if (-not (Test-Path -LiteralPath $secretsPath)) {
    [ordered]@{
        cloudflareTunnelToken = 'PASTE_TOKEN_HERE'
        publicHostname = ''
        localPort = 19057
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $secretsPath -Encoding UTF8
}

$runtimeStatePath = Join-Path $stateDir 'runtime-state.json'
if (-not (Test-Path -LiteralPath $runtimeStatePath)) {
    [ordered]@{
        apps = @{}
        activeLocalPort = $null
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $runtimeStatePath -Encoding UTF8
}

$shortcutPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) 'MasterApp Lazy.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $scriptRoot
$shortcut.Save()

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if ($isAdmin) {
    New-NetFirewallRule -DisplayName 'MasterApp Lazy Local' -Direction Inbound -Program $exePath -Action Allow -Profile Private -ErrorAction SilentlyContinue | Out-Null
}

Write-Host "Settings: $settingsPath"
Write-Host "Drive folder: $driveRoot"
Write-Host "Shortcut: $shortcutPath"
if (-not $isAdmin) {
    Write-Host 'Firewall rule was skipped because setup is not elevated. If the phone cannot connect, allow MasterApp on Private networks.'
}
