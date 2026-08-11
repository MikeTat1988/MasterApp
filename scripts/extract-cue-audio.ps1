param(
    [Parameter(Mandatory = $true)]
    [string]$CuePath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$cueFile = Get-Item -LiteralPath $CuePath
$cueLines = Get-Content -LiteralPath $cueFile.FullName
$binName = ($cueLines | Select-String '^\s*FILE\s+"([^"]+)"\s+BINARY\s*$').Matches.Groups[1].Value
if (-not $binName) {
    throw "The CUE file does not contain a supported BINARY file entry."
}

$binPath = Join-Path $cueFile.DirectoryName $binName
if (-not (Test-Path -LiteralPath $binPath)) {
    throw "BIN file not found: $binPath"
}

$tracks = [System.Collections.Generic.List[object]]::new()
$currentTrack = $null
foreach ($line in $cueLines) {
    if ($line -match '^\s*TRACK\s+(\d+)\s+(\S+)') {
        $currentTrack = [pscustomobject]@{
            Number = [int]$Matches[1]
            Type = $Matches[2]
            StartSector = $null
        }
        $tracks.Add($currentTrack)
        continue
    }

    if ($currentTrack -and $line -match '^\s*INDEX\s+01\s+(\d+):(\d+):(\d+)') {
        $minutes = [int]$Matches[1]
        $seconds = [int]$Matches[2]
        $frames = [int]$Matches[3]
        $currentTrack.StartSector = (($minutes * 60) + $seconds) * 75 + $frames
    }
}

$sectorSize = 2352L
$binLength = (Get-Item -LiteralPath $binPath).Length
if (($binLength % $sectorSize) -ne 0) {
    throw "BIN length is not a multiple of $sectorSize bytes."
}

$totalSectors = [long]($binLength / $sectorSize)
$audioTracks = @($tracks | Where-Object { $_.Type -eq 'AUDIO' -and $null -ne $_.StartSector })
if ($audioTracks.Count -eq 0) {
    throw "No AUDIO tracks with INDEX 01 were found."
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$input = [System.IO.File]::OpenRead($binPath)
try {
    for ($i = 0; $i -lt $audioTracks.Count; $i++) {
        $track = $audioTracks[$i]
        $nextTrack = $tracks | Where-Object { $_.Number -gt $track.Number -and $null -ne $_.StartSector } | Select-Object -First 1
        $endSector = if ($nextTrack) { [long]$nextTrack.StartSector } else { $totalSectors }
        $startSector = [long]$track.StartSector
        $dataLength = ($endSector - $startSector) * $sectorSize
        if ($dataLength -le 0 -or $dataLength -gt [uint32]::MaxValue) {
            throw "Invalid audio length for track $($track.Number): $dataLength"
        }

        $outputPath = Join-Path $OutputDirectory ('track{0:D2}.wav' -f $track.Number)
        $output = [System.IO.File]::Create($outputPath)
        try {
            $writer = [System.IO.BinaryWriter]::new($output, [System.Text.Encoding]::ASCII, $true)
            $writer.Write([System.Text.Encoding]::ASCII.GetBytes('RIFF'))
            $writer.Write([uint32](36 + $dataLength))
            $writer.Write([System.Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
            $writer.Write([uint32]16)
            $writer.Write([uint16]1)
            $writer.Write([uint16]2)
            $writer.Write([uint32]44100)
            $writer.Write([uint32]176400)
            $writer.Write([uint16]4)
            $writer.Write([uint16]16)
            $writer.Write([System.Text.Encoding]::ASCII.GetBytes('data'))
            $writer.Write([uint32]$dataLength)
            $writer.Flush()

            $input.Position = $startSector * $sectorSize
            $remaining = [long]$dataLength
            $buffer = [byte[]]::new(1MB)
            while ($remaining -gt 0) {
                $count = [int][Math]::Min($buffer.Length, $remaining)
                $read = $input.Read($buffer, 0, $count)
                if ($read -le 0) {
                    throw "Unexpected end of BIN while extracting track $($track.Number)."
                }
                $output.Write($buffer, 0, $read)
                $remaining -= $read
            }
        }
        finally {
            $output.Dispose()
        }

        Get-Item -LiteralPath $outputPath | Select-Object Name, Length
    }
}
finally {
    $input.Dispose()
}
