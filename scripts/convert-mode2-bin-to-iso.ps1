param(
    [Parameter(Mandatory = $true)]
    [string]$BinPath,

    [Parameter(Mandatory = $true)]
    [string]$IsoPath,

    [Parameter(Mandatory = $true)]
    [int64]$SectorCount
)

$sectorSize = 2352
$dataOffset = 24
$dataSize = 2048

$resolvedBinPath = (Resolve-Path -LiteralPath $BinPath).Path
$resolvedIsoPath = [IO.Path]::GetFullPath($IsoPath)
$expectedInputLength = $SectorCount * $sectorSize
$inputInfo = Get-Item -LiteralPath $resolvedBinPath

if ($inputInfo.Length -lt $expectedInputLength) {
    throw "BIN is shorter than the requested data track: $($inputInfo.Length) < $expectedInputLength bytes."
}

$input = [IO.File]::Open($resolvedBinPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$output = [IO.File]::Open($resolvedIsoPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
$sector = [byte[]]::new($sectorSize)

try {
    for ($index = 0L; $index -lt $SectorCount; $index++) {
        $read = 0
        while ($read -lt $sectorSize) {
            $count = $input.Read($sector, $read, $sectorSize - $read)
            if ($count -eq 0) {
                throw "Unexpected end of BIN at sector $index."
            }
            $read += $count
        }

        if ($sector[15] -ne 2) {
            throw "Sector $index is not Mode 2 (mode byte: $($sector[15]))."
        }

        $output.Write($sector, $dataOffset, $dataSize)
    }
}
finally {
    $output.Dispose()
    $input.Dispose()
}

$isoInfo = Get-Item -LiteralPath $resolvedIsoPath
[pscustomobject]@{
    BinPath = $resolvedBinPath
    IsoPath = $isoInfo.FullName
    SectorCount = $SectorCount
    IsoLength = $isoInfo.Length
}
