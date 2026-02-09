Add-Type -AssemblyName System.Drawing

$pngPath = "C:\Users\user\.gemini\antigravity\brain\a2f4f660-57ed-4e71-a372-6d5ab3873455\lucilink_window_indigo_1770478260248.png"
$icoPath = "c:\Users\user\Documents\Lucilink\LuciLink.Client\Resources\App.ico"

$png = [System.Drawing.Image]::FromFile($pngPath)
$resized = New-Object System.Drawing.Bitmap($png, 256, 256)

$ms = New-Object System.IO.MemoryStream
$resized.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $ms.ToArray()

$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICO header
$bw.Write([byte[]]@(0, 0))        # Reserved
$bw.Write([Int16]1)                # Type (1 = ICO)
$bw.Write([Int16]1)                # Number of images

# ICO directory entry
$bw.Write([byte]0)                 # Width (0 = 256)
$bw.Write([byte]0)                 # Height (0 = 256)
$bw.Write([byte]0)                 # Color palette
$bw.Write([byte]0)                 # Reserved
$bw.Write([Int16]1)                # Color planes
$bw.Write([Int16]32)               # Bits per pixel
$bw.Write([Int32]$pngBytes.Length)  # Image data size
$bw.Write([Int32]22)               # Image data offset

# Image data
$bw.Write($pngBytes)

$bw.Flush()
$fs.Close()
$resized.Dispose()
$png.Dispose()
$ms.Dispose()

Write-Host "ICO created successfully at $icoPath"
