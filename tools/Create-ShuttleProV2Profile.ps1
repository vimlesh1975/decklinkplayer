param(
    [switch]$SkipInstallFolder,
    [switch]$SkipActiveDatabase
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$templatePath = "C:\Program Files (x86)\Contour Shuttle\Settings\ShuttlePRO v2\Audacity.pref"
$contourProfilePath = "C:\Program Files (x86)\Contour Shuttle\Settings\ShuttlePRO v2\DeckLinkPlayer.pref"
$activeDatabasePath = "C:\ProgramData\Contour Design\CDIShuttle.pref"
$projectProfileDir = Join-Path $repoRoot "ShuttleProfiles"
$projectProfilePath = Join-Path $projectProfileDir "DeckLinkPlayer.pref"
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

function Write-UInt16LE {
    param([byte[]]$Bytes, [int]$Offset, [UInt16]$Value)
    $encoded = [BitConverter]::GetBytes($Value)
    [Array]::Copy($encoded, 0, $Bytes, $Offset, 2)
}

function Write-Int16LE {
    param([byte[]]$Bytes, [int]$Offset, [Int16]$Value)
    $encoded = [BitConverter]::GetBytes($Value)
    [Array]::Copy($encoded, 0, $Bytes, $Offset, 2)
}

function Write-FixedUnicodeString {
    param([byte[]]$Bytes, [int]$Offset, [int]$ByteLength, [string]$Value)

    [Array]::Clear($Bytes, $Offset, $ByteLength)
    $encoded = [Text.Encoding]::Unicode.GetBytes($Value)
    $copyLength = [Math]::Min($encoded.Length, $ByteLength - 2)
    [Array]::Copy($encoded, 0, $Bytes, $Offset, $copyLength)
}

function Read-FixedUnicodeString {
    param([byte[]]$Bytes, [int]$Offset, [int]$ByteLength)

    $chars = New-Object System.Collections.Generic.List[char]
    for ($i = $Offset; $i -lt [Math]::Min($Offset + $ByteLength, $Bytes.Length - 1); $i += 2) {
        $code = [BitConverter]::ToUInt16($Bytes, $i)
        if ($code -eq 0) {
            break
        }

        $chars.Add([char]$code)
    }

    return -join $chars
}

function Set-KeyAction {
    param(
        [byte[]]$Bytes,
        [int]$RecordOffset,
        [UInt16]$Modifier,
        [byte]$Key,
        [Int16]$Repeat,
        [string]$Label
    )

    Write-UInt16LE $Bytes ($RecordOffset + 4) 8
    Write-UInt16LE $Bytes ($RecordOffset + 6) $Modifier
    $Bytes[$RecordOffset + 8] = $Key
    $Bytes[$RecordOffset + 9] = 0xFF
    Write-Int16LE $Bytes ($RecordOffset + 10) $Repeat
    [Array]::Clear($Bytes, $RecordOffset + 12, 0x48)
    Write-FixedUnicodeString $Bytes ($RecordOffset + 0x54) 0x3C $Label
}

function Stop-ContourProcesses {
    $stopped = New-Object System.Collections.Generic.List[string]
    Get-Process -Name ShuttleHelper, ShuttleEngine -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Path) {
            $stopped.Add($_.Path)
        }

        Stop-Process -Id $_.Id -Force
    }

    if ($stopped.Count -gt 0) {
        Start-Sleep -Milliseconds 700
    }

    return $stopped | Select-Object -Unique
}

function Start-ContourProcesses {
    param([string[]]$ProcessPaths)

    foreach ($path in $ProcessPaths | Sort-Object) {
        if (Test-Path -LiteralPath $path) {
            Start-Process -FilePath $path -WindowStyle Hidden
        }
    }
}

if (!(Test-Path -LiteralPath $templatePath)) {
    throw "Template profile not found: $templatePath"
}

New-Item -ItemType Directory -Force -Path $projectProfileDir | Out-Null

[byte[]]$profile = ([byte[]][IO.File]::ReadAllBytes($templatePath)).Clone()
Write-FixedUnicodeString $profile 0x14 0x40 "DeckLinkPlayer"
Write-FixedUnicodeString $profile 0x54 0x40 "decklinkplayer_*.exe"

# Jog wheel.
Set-KeyAction $profile 0x01E0 0 0x25 0 "Jog -1 frame"
Set-KeyAction $profile 0x0270 0 0x27 0 "Jog +1 frame"

# Shuttle ring. Modifier 5 = Ctrl+Shift, modifier 20 = Ctrl+Alt.
# Repeat values mirror the factory shuttle template so the app receives refresh keys while the ring is held.
Set-KeyAction $profile 0x04B0 5 0x39 1 "Shuttle -20x"
Set-KeyAction $profile 0x0540 5 0x38 3 "Shuttle -10x"
Set-KeyAction $profile 0x05D0 5 0x37 4 "Shuttle -5x"
Set-KeyAction $profile 0x0660 5 0x36 7 "Shuttle -2x"
Set-KeyAction $profile 0x06F0 5 0x34 10 "Shuttle -1x"
Set-KeyAction $profile 0x0780 5 0x32 20 "Shuttle -0.50x"
Set-KeyAction $profile 0x0810 5 0x31 50 "Shuttle -0.25x"
Set-KeyAction $profile 0x08A0 20 0x30 0 "Shuttle 0x"
Set-KeyAction $profile 0x0930 20 0x31 50 "Shuttle +0.25x"
Set-KeyAction $profile 0x09C0 20 0x32 20 "Shuttle +0.50x"
Set-KeyAction $profile 0x0A50 20 0x34 10 "Shuttle +1x"
Set-KeyAction $profile 0x0AE0 20 0x36 7 "Shuttle +2x"
Set-KeyAction $profile 0x0B70 20 0x37 4 "Shuttle +5x"
Set-KeyAction $profile 0x0C00 20 0x38 3 "Shuttle +10x"
Set-KeyAction $profile 0x0C90 20 0x39 1 "Shuttle +20x"

# Buttons.
Set-KeyAction $profile 0x23A0 0 0x70 0 "Stop"
Set-KeyAction $profile 0x2430 0 0x20 0 "Pause / Resume"
Set-KeyAction $profile 0x24C0 0 0x71 0 "Play playlist row"
Set-KeyAction $profile 0x2550 0 0x72 0 "Cue playlist row"
Set-KeyAction $profile 0x25E0 0 0x74 0 "Cue next"
Set-KeyAction $profile 0x2670 0 0x75 0 "Play next"
Set-KeyAction $profile 0x2700 0 0x49 0 "Mark IN"
Set-KeyAction $profile 0x2790 0 0x4F 0 "Mark OUT"
Set-KeyAction $profile 0x2820 0 0x24 0 "Go to IN"
Set-KeyAction $profile 0x28B0 0 0x23 0 "Go to OUT"
Set-KeyAction $profile 0x2940 4 0x0D 0 "Play media clip"
Set-KeyAction $profile 0x29D0 0 0x7A 0 "Previous play"
Set-KeyAction $profile 0x2A60 0 0x7B 0 "Next play"
Set-KeyAction $profile 0x2AF0 16 0x25 0 "Seek -1 sec"
Set-KeyAction $profile 0x2B80 16 0x27 0 "Seek +1 sec"

[IO.File]::WriteAllBytes($projectProfilePath, $profile)

$stoppedProcesses = Stop-ContourProcesses
try {
    if (!$SkipInstallFolder) {
        try {
            if (Test-Path -LiteralPath $contourProfilePath) {
                Copy-Item -LiteralPath $contourProfilePath -Destination "$contourProfilePath.bak_$timestamp" -Force
            }

            [IO.File]::WriteAllBytes($contourProfilePath, $profile)
            Write-Output "Installed Contour settings-folder profile: $contourProfilePath"
        }
        catch [UnauthorizedAccessException] {
            Write-Warning "Could not write Program Files profile copy. Continuing with active database update. Run this script as Administrator later if you want the settings-folder copy."
        }
    }

    if (!$SkipActiveDatabase) {
        if (!(Test-Path -LiteralPath $activeDatabasePath)) {
            throw "Active Contour database not found: $activeDatabasePath"
        }

        Copy-Item -LiteralPath $activeDatabasePath -Destination "$activeDatabasePath.bak_$timestamp" -Force

        [byte[]]$database = [IO.File]::ReadAllBytes($activeDatabasePath)
        $blockSize = 11988
        $trailingSize = $database.Length % $blockSize
        $mainLength = $database.Length - $trailingSize
        $profileCount = [int]($mainLength / $blockSize)
        [byte[]]$databaseProfile = New-Object byte[] $blockSize
        [Array]::Copy($profile, 0, $databaseProfile, 0, $blockSize)

        $existingOffset = -1
        $defaultOffset = -1
        for ($index = 0; $index -lt $profileCount; $index++) {
            $offset = $index * $blockSize
            $name = Read-FixedUnicodeString $database ($offset + 0x14) 0x40
            $exe = Read-FixedUnicodeString $database ($offset + 0x54) 0x40
            if ($name -eq "DeckLinkPlayer" -or $exe -eq "decklinkplayer_*.exe") {
                $existingOffset = $offset
                break
            }

            if ($name -eq "" -and $exe -eq "*") {
                $defaultOffset = $offset
            }
        }

        if ($existingOffset -ge 0) {
            [Array]::Copy($databaseProfile, 0, $database, $existingOffset, $blockSize)
            [IO.File]::WriteAllBytes($activeDatabasePath, $database)
            Write-Output "Updated existing active Contour profile at offset $existingOffset."
        }
        else {
            $insertOffset = if ($defaultOffset -ge 0) { $defaultOffset } else { $mainLength }
            [byte[]]$updated = New-Object byte[] ($database.Length + $blockSize)
            [Array]::Copy($database, 0, $updated, 0, $insertOffset)
            [Array]::Copy($databaseProfile, 0, $updated, $insertOffset, $blockSize)
            [Array]::Copy($database, $insertOffset, $updated, $insertOffset + $blockSize, $database.Length - $insertOffset)
            [IO.File]::WriteAllBytes($activeDatabasePath, $updated)
            Write-Output "Inserted active Contour profile at offset $insertOffset."
        }
    }
}
finally {
    Start-ContourProcesses $stoppedProcesses
}

Write-Output "Wrote project profile: $projectProfilePath"
if ($SkipInstallFolder) {
    Write-Output "Skipped Contour settings-folder profile copy."
}
Write-Output "Active database backup suffix: bak_$timestamp"
