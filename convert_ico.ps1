$pngPath = "C:\Users\yeyil\Documents\OmenFlow\OmenFlow.App\icons\omenflowicon.png"
$icoPath = "C:\Users\yeyil\Documents\OmenFlow\OmenFlow.App\icons\omenflow.ico"

$pngBytes = [System.IO.File]::ReadAllBytes($pngPath)
$pngSize = $pngBytes.Length

$icoHeader = New-Object byte[] 22
$icoHeader[0] = 0; $icoHeader[1] = 0; # Reserved
$icoHeader[2] = 1; $icoHeader[3] = 0; # Type (1 = ICO)
$icoHeader[4] = 1; $icoHeader[5] = 0; # Image Count (1)

# Width and Height (0 means 256)
$icoHeader[6] = 0; $icoHeader[7] = 0; 
$icoHeader[8] = 0; # Color count
$icoHeader[9] = 0; # Reserved
$icoHeader[10] = 1; $icoHeader[11] = 0; # Planes
$icoHeader[12] = 32; $icoHeader[13] = 0; # Bits per pixel

# Size (4 bytes, little endian)
$sizeBytes = [System.BitConverter]::GetBytes([int]$pngSize)
$icoHeader[14] = $sizeBytes[0]; $icoHeader[15] = $sizeBytes[1]
$icoHeader[16] = $sizeBytes[2]; $icoHeader[17] = $sizeBytes[3]

# Offset (22, 4 bytes, little endian)
$icoHeader[18] = 22; $icoHeader[19] = 0
$icoHeader[20] = 0; $icoHeader[21] = 0

$icoBytes = New-Object byte[] ($icoHeader.Length + $pngBytes.Length)
[System.Array]::Copy($icoHeader, 0, $icoBytes, 0, $icoHeader.Length)
[System.Array]::Copy($pngBytes, 0, $icoBytes, $icoHeader.Length, $pngBytes.Length)

[System.IO.File]::WriteAllBytes($icoPath, $icoBytes)
