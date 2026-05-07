param(
  [string]$Root = ""
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Root)) {
  $Root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}

$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\MasterAppWatchdog', [ref]$createdNew)
if (-not $createdNew) {
  exit 0
}

function Read-JsonFile {
  param([string]$Path)
  if (-not (Test-Path -LiteralPath $Path)) {
    return $null
  }

  try {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
  } catch {
    return $null
  }
}

function Write-JsonFile {
  param(
    [string]$Path,
    [object]$Value
  )

  $directory = Split-Path -Parent $Path
  if (-not (Test-Path -LiteralPath $directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
  }

  $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Remove-JsonFile {
  param([string]$Path)
  if (Test-Path -LiteralPath $Path) {
    Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
  }
}

function Get-LocalPort {
  param([string]$SecretsPath)
  $secrets = Read-JsonFile -Path $SecretsPath
  if ($null -ne $secrets -and $secrets.PSObject.Properties.Name -contains 'LocalPort' -and $secrets.LocalPort) {
    return [int]$secrets.LocalPort
  }

  return 19057
}

function Test-MasterAppHealthy {
  param([int]$Port)
  try {
    $response = Invoke-WebRequest -Uri ("http://127.0.0.1:{0}/healthz" -f $Port) -UseBasicParsing -TimeoutSec 5
    return $response.StatusCode -ge 200 -and $response.StatusCode -lt 500
  } catch {
    return $false
  }
}

function Was-ExplicitQuit {
  param([object]$Intent)
  return $null -ne $Intent -and $Intent.PSObject.Properties.Name -contains 'Reason' -and [string]::Equals([string]$Intent.Reason, 'quit', [System.StringComparison]::OrdinalIgnoreCase)
}

function Can-AttemptRestart {
  param(
    [object]$State,
    [datetimeoffset]$NowUtc
  )

  if ($null -eq $State) {
    return $true
  }

  if ([int]$State.ConsecutiveLaunchFailures -lt 3) {
    return $true
  }

  if (-not $State.LastFailureAtUtc) {
    return $false
  }

  return ($NowUtc - ([datetimeoffset]$State.LastFailureAtUtc)).TotalHours -ge 2
}

function Register-LaunchFailure {
  param(
    [object]$State,
    [datetimeoffset]$NowUtc
  )

  $failures = 0
  $firstFailure = $null
  if ($null -ne $State) {
    $failures = [int]$State.ConsecutiveLaunchFailures
    if ($State.FirstFailureAtUtc) {
      $firstFailure = [datetimeoffset]$State.FirstFailureAtUtc
    }
    if ($State.LastFailureAtUtc -and ($NowUtc - ([datetimeoffset]$State.LastFailureAtUtc)).TotalHours -ge 2) {
      $failures = 0
      $firstFailure = $NowUtc
    }
  }

  if ($null -eq $firstFailure) {
    $firstFailure = $NowUtc
  }

  return [pscustomobject]@{
    ConsecutiveLaunchFailures = $failures + 1
    FirstFailureAtUtc = $firstFailure.ToString('O')
    LastFailureAtUtc = $NowUtc.ToString('O')
    LastLaunchAttemptAtUtc = $NowUtc.ToString('O')
    LastSuccessfulLaunchAtUtc = if ($null -ne $State) { $State.LastSuccessfulLaunchAtUtc } else { $null }
  }
}

function Register-LaunchSuccess {
  param(
    [object]$State,
    [datetimeoffset]$NowUtc
  )

  return [pscustomobject]@{
    ConsecutiveLaunchFailures = 0
    FirstFailureAtUtc = $null
    LastFailureAtUtc = $null
    LastLaunchAttemptAtUtc = $NowUtc.ToString('O')
    LastSuccessfulLaunchAtUtc = $NowUtc.ToString('O')
  }
}

$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$stateDirectory = Join-Path $localAppData 'MasterApp\State'
$shutdownIntentPath = Join-Path $stateDirectory 'shutdown-intent.json'
$watchdogStatePath = Join-Path $stateDirectory 'watchdog-state.json'
$secretsPath = Join-Path $stateDirectory 'secrets.json'
$runScript = Join-Path $Root 'scripts\run-masterapp.bat'
$sleepSeconds = 1800

try {
  while ($true) {
    $port = Get-LocalPort -SecretsPath $secretsPath
    $healthy = Test-MasterAppHealthy -Port $port

    if ($healthy) {
      $state = Read-JsonFile -Path $watchdogStatePath
      if ($null -ne $state -and [int]$state.ConsecutiveLaunchFailures -gt 0) {
        Write-JsonFile -Path $watchdogStatePath -Value (Register-LaunchSuccess -State $state -NowUtc ([datetimeoffset]::UtcNow))
      }

      Start-Sleep -Seconds $sleepSeconds
      continue
    }

    $shutdownIntent = Read-JsonFile -Path $shutdownIntentPath
    if (Was-ExplicitQuit -Intent $shutdownIntent) {
      Start-Sleep -Seconds $sleepSeconds
      continue
    }

    $nowUtc = [datetimeoffset]::UtcNow
    $state = Read-JsonFile -Path $watchdogStatePath
    if (-not (Can-AttemptRestart -State $state -NowUtc $nowUtc)) {
      Start-Sleep -Seconds $sleepSeconds
      continue
    }

    Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', ('"{0}" --no-watchdog' -f $runScript) -WorkingDirectory $Root -WindowStyle Hidden
    Start-Sleep -Seconds 90

    if (Test-MasterAppHealthy -Port (Get-LocalPort -SecretsPath $secretsPath)) {
      Remove-JsonFile -Path $shutdownIntentPath
      Write-JsonFile -Path $watchdogStatePath -Value (Register-LaunchSuccess -State $state -NowUtc ([datetimeoffset]::UtcNow))
    } else {
      Write-JsonFile -Path $watchdogStatePath -Value (Register-LaunchFailure -State $state -NowUtc ([datetimeoffset]::UtcNow))
    }

    Start-Sleep -Seconds $sleepSeconds
  }
}
finally {
  if ($null -ne $mutex) {
    try {
      $mutex.ReleaseMutex() | Out-Null
    } catch {
    }
    $mutex.Dispose()
  }
}
