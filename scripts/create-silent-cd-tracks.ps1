param(
    [Parameter(Mandatory = $true)]
    [string]$MusicDirectory
)

$resolvedMusic = [System.IO.Path]::GetFullPath($MusicDirectory)
$trackInfo = Join-Path $resolvedMusic 'tracklen.nfo'
if (-not (Test-Path -LiteralPath $trackInfo)) {
    throw "Missing track metadata: $trackInfo"
}

$sampleRate = 8000
$bitsPerSample = 8
$channels = 1
$blockAlign = $channels * ($bitsPerSample / 8)
$byteRate = $sampleRate * $blockAlign

foreach ($line in Get-Content -LiteralPath $trackInfo) {
    if ($line -notmatch '^track=(\d+)\s+type=w\s+pos=\d+\s+len=(\d+)$') {
        continue
    }

    $track = [int]$Matches[1]
    $durationSeconds = [int]$Matches[2]
    $dataLength = $durationSeconds * $byteRate
    $path = Join-Path $resolvedMusic ('track{0:D2}.wav' -f $track)

    $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    try {
        $writer = [System.IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([System.Text.Encoding]::ASCII.GetBytes('RIFF'))
            $writer.Write([int](36 + $dataLength))
            $writer.Write([System.Text.Encoding]::ASCII.GetBytes('WAVE'))
            $writer.Write([System.Text.Encoding]::ASCII.GetBytes('fmt '))
            $writer.Write([int]16)
            $writer.Write([int16]1)
            $writer.Write([int16]$channels)
            $writer.Write([int]$sampleRate)
            $writer.Write([int]$byteRate)
            $writer.Write([int16]$blockAlign)
            $writer.Write([int16]$bitsPerSample)
            $writer.Write([System.Text.Encoding]::ASCII.GetBytes('data'))
            $writer.Write([int]$dataLength)

            $silence = [byte[]]::new(65536)
            for ($index = 0; $index -lt $silence.Length; $index++) {
                $silence[$index] = 128
            }
            $remaining = $dataLength
            while ($remaining -gt 0) {
                $count = [Math]::Min($remaining, $silence.Length)
                $writer.Write($silence, 0, $count)
                $remaining -= $count
            }
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

Get-ChildItem -LiteralPath $resolvedMusic -Filter 'track*.wav' |
    Sort-Object Name |
    Select-Object Name, Length
