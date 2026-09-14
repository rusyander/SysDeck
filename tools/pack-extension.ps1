#requires -Version 5.1
<#
.SYNOPSIS
  Builds the browser extension: unpacked folders dist\extension-chromium, dist\extension-firefox and store zips.
.PARAMETER RegenerateIcons
  Re-create extension\src\icons\icon-{16,32,48,128}.png from the repo icon.ico (also done when any is missing).
#>
[CmdletBinding()]
param(
    [switch]$RegenerateIcons
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$repo = Split-Path -Parent $PSScriptRoot
$extRoot = Join-Path $repo 'extension'
$src = Join-Path $extRoot 'src'
$dist = Join-Path $repo 'dist'
$iconSizes = @(16, 32, 48, 128)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---- icons: PNG frames of icon.ico, exact size when present, else downscaled from the largest frame ----
function Get-IcoFrames([byte[]]$bytes) {
    $count = [BitConverter]::ToUInt16($bytes, 4)
    $frames = @()
    for ($i = 0; $i -lt $count; $i++) {
        $o = 6 + $i * 16
        $w = [int]$bytes[$o]; if ($w -eq 0) { $w = 256 }
        $len = [BitConverter]::ToInt32($bytes, $o + 8)
        $off = [BitConverter]::ToInt32($bytes, $o + 12)
        $data = New-Object byte[] $len
        [Array]::Copy($bytes, $off, $data, 0, $len)
        $isPng = $len -gt 8 -and $data[0] -eq 0x89 -and $data[1] -eq 0x50 -and $data[2] -eq 0x4E -and $data[3] -eq 0x47
        $frames += New-Object psobject -Property @{ Size = $w; Data = $data; IsPng = $isPng }
    }
    return $frames
}

function Save-Scaled([System.Drawing.Image]$image, [int]$size, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.Clear([System.Drawing.Color]::Transparent)
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $g.DrawImage($image, 0, 0, $size, $size)
        } finally { $g.Dispose() }
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bmp.Dispose() }
}

function Write-Icons {
    $ico = Join-Path $repo 'icon.ico'
    if (-not (Test-Path -LiteralPath $ico)) { throw "icon.ico not found: $ico" }
    $iconDir = Join-Path $src 'icons'
    New-Item -ItemType Directory -Force -Path $iconDir | Out-Null
    $frames = Get-IcoFrames ([System.IO.File]::ReadAllBytes($ico))
    $largest = $frames | Sort-Object Size -Descending | Select-Object -First 1
    foreach ($size in $iconSizes) {
        $out = Join-Path $iconDir ("icon-{0}.png" -f $size)
        $exact = $frames | Where-Object { $_.Size -eq $size -and $_.IsPng } | Select-Object -First 1
        if ($exact) {
            [System.IO.File]::WriteAllBytes($out, $exact.Data)
        } elseif ($largest.IsPng) {
            $ms = New-Object System.IO.MemoryStream(, $largest.Data)
            try {
                $img = [System.Drawing.Image]::FromStream($ms)
                try { Save-Scaled $img $size $out } finally { $img.Dispose() }
            } finally { $ms.Dispose() }
        } else {
            $icon = New-Object System.Drawing.Icon($ico, $size, $size)
            try {
                $img = $icon.ToBitmap()
                try { Save-Scaled $img $size $out } finally { $img.Dispose() }
            } finally { $icon.Dispose() }
        }
        Write-Host ("icon {0}px -> {1}" -f $size, $out)
    }
}

$missingIcon = $iconSizes | Where-Object { -not (Test-Path -LiteralPath (Join-Path $src ("icons\icon-{0}.png" -f $_))) }
if ($RegenerateIcons -or $missingIcon) { Write-Icons }

# ---- assemble ----
$common = @('rules.js', 'bridge.js', 'background.js', 'menu.js', 'popup.html', 'popup.css', 'popup.js')
$targets = @(
    @{ Name = 'chromium'; Manifest = 'manifest.chromium.json'; Files = $common + @('intercept-chromium.js'); StoreStripKey = $true },
    @{ Name = 'firefox'; Manifest = 'manifest.firefox.json'; Files = $common + @('intercept-firefox.js'); StoreStripKey = $false }
)

function Copy-Target($t) {
    $outDir = Join-Path $dist ("extension-" + $t.Name)
    # Only ever wipe our own output folder inside dist\.
    if ((Split-Path -Leaf $outDir) -notlike 'extension-*') { throw "refusing to clean $outDir" }
    if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    foreach ($f in $t.Files) {
        $from = Join-Path $src $f
        if (-not (Test-Path -LiteralPath $from)) { throw "missing source file: $from" }
        Copy-Item -LiteralPath $from -Destination (Join-Path $outDir $f)
    }
    foreach ($dir in @('_locales', 'icons')) {
        Copy-Item -LiteralPath (Join-Path $src $dir) -Destination (Join-Path $outDir $dir) -Recurse
    }
    Copy-Item -LiteralPath (Join-Path $extRoot $t.Manifest) -Destination (Join-Path $outDir 'manifest.json')

    # Every file the manifest points at must exist.
    $m = Get-Content -LiteralPath (Join-Path $outDir 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $refs = @()
    $refs += $m.icons.PSObject.Properties | ForEach-Object { $_.Value }
    $refs += $m.action.default_icon.PSObject.Properties | ForEach-Object { $_.Value }
    $refs += $m.action.default_popup
    if ($m.background.PSObject.Properties['service_worker']) { $refs += $m.background.service_worker }
    if ($m.background.PSObject.Properties['scripts']) { $refs += $m.background.scripts }
    foreach ($r in $refs) {
        if (-not (Test-Path -LiteralPath (Join-Path $outDir $r))) { throw "manifest reference missing in $outDir : $r" }
    }
    return $outDir
}

function New-StoreZip([string]$folder, [string]$zipPath, [bool]$stripKey) {
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    $root = (Resolve-Path -LiteralPath $folder).Path.TrimEnd('\') + '\'
    $fs = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $files = Get-ChildItem -LiteralPath $folder -Recurse -File | Sort-Object FullName
            foreach ($file in $files) {
                # Forward slashes: stores reject backslash entry names (Compress-Archive on PS 5.1 writes them).
                $rel = $file.FullName.Substring($root.Length).Replace('\', '/')
                $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
                if ($rel -eq 'manifest.json' -and $stripKey) {
                    # Store builds get their id from the store; "key" is for the unpacked build only.
                    $text = $utf8NoBom.GetString($bytes)
                    $text = [regex]::Replace($text, '(?m)^[ \t]*"key"[ \t]*:[ \t]*"[^"]*",[ \t]*\r?\n', '')
                    $parsed = $text | ConvertFrom-Json
                    if ($parsed.PSObject.Properties['key']) { throw 'failed to strip manifest key for the store zip' }
                    $bytes = $utf8NoBom.GetBytes($text)
                }
                $entry = $zip.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
                $stream = $entry.Open()
                try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $fs.Dispose() }
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
foreach ($t in $targets) {
    $folder = Copy-Target $t
    $zipPath = Join-Path $dist ("extension-{0}.zip" -f $t.Name)
    New-StoreZip $folder $zipPath $t.StoreStripKey

    $check = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $names = @($check.Entries | ForEach-Object { $_.FullName })
        if ($names -notcontains 'manifest.json') { throw "manifest.json not at zip root: $zipPath" }
        if (@($names | Where-Object { $_ -like '*\*' }).Count -gt 0) { throw "backslash entry names in $zipPath" }
        $fileCount = @(Get-ChildItem -LiteralPath $folder -Recurse -File).Count
        Write-Host ("{0}: {1} files -> {2}; zip {3} entries, {4:N0} bytes -> {5}" -f $t.Name, $fileCount, $folder, $names.Count, (Get-Item -LiteralPath $zipPath).Length, $zipPath)
    } finally { $check.Dispose() }
}
