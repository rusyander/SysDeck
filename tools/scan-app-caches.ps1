# SysDeck - scan-app-caches.ps1
# Ищет на текущей машине папки-кэши установленных программ и сверяет их с каталогом очистки
# (src\*.cs). Скрипт ТОЛЬКО ЧИТАЕТ: единственная запись - файл отчёта .agent\tmp\catalog-scan-<дата>.md.
# Требуется Windows PowerShell 5.1 (входит в состав Windows), внешних модулей нет.
#
# Запуск:
#   powershell -ExecutionPolicy Bypass -File tools\scan-app-caches.ps1
#   powershell -ExecutionPolicy Bypass -File tools\scan-app-caches.ps1 -MinMB 0.5 -Depth 4

[CmdletBinding()]
param(
    # Каталог с исходниками: из них вытаскиваются уже покрытые пути (литералы AddDir/Path.Combine).
    [string] $SrcDir,
    # Файл отчёта. По умолчанию .agent\tmp\catalog-scan-<yyyy-MM-dd>.md рядом с репозиторием.
    [string] $OutFile,
    # Глубина обхода от корня (LOCALAPPDATA\Vendor\App\Cache = 3).
    [int]    $Depth = 3,
    # Папки мельче порога в отчёт не попадают: их удаление не окупает риска.
    [double] $MinMB = 1,
    # Ограничение на длину таблицы, чтобы отчёт оставался читаемым.
    [int]    $Top = 400
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# ---------------------------------------------------------------- пути ----

$repo = Split-Path -Parent $PSScriptRoot
if (-not $SrcDir)  { $SrcDir  = Join-Path $repo 'src' }
if (-not $OutFile) {
    $stamp = (Get-Date).ToString('yyyy-MM-dd')
    $OutFile = Join-Path $repo (".agent\tmp\catalog-scan-$stamp.md")
}

$roots = @(
    @{ Name = 'lad'; Path = $env:LOCALAPPDATA },
    @{ Name = 'ad';  Path = $env:APPDATA },
    @{ Name = 'pd';  Path = $env:ProgramData }
)

# Поддеревья, которые смотреть незачем: Packages разбирает AddStoreAppCaches, Temp и INetCache
# целиком уже в категории «Системный мусор», а обход WebCache/Explorer стоит минуты и не даёт
# ни одной новой цели.
$skip = @(
    'Packages', 'Temp', 'Microsoft\Windows\INetCache', 'Microsoft\Windows\WebCache',
    'Microsoft\Windows\Explorer', 'Microsoft\Windows\Caches', 'Microsoft\Windows\History',
    'Application Data', 'Temporary Internet Files', 'VirtualStore'
)

# Имя папки считаем кэшем, если в нём есть «cache» либо оно целиком совпадает с типовым
# именем свалки. Слишком широкий фильтр здесь лучше узкого: отчёт читает человек, а вот
# пропущенная папка не попадёт в каталог никогда.
$exactNames = '^(logs?|crash(es|dumps|pad|reports)?|dumps?|minidumps?|te?mp|temporary|thumbnails?|thumbs|reports|profiler_data|shadercache|webcache|htmlcache|blob_storage|gpucache|cachedata)$'

function Test-CacheName([string] $name) {
    if ($name -match '(?i)cache') { return $true }
    return ($name -match "(?i)$exactNames")
}

function Test-Skipped([string] $rel) {
    foreach ($s in $skip) {
        if ($rel -eq $s -or $rel.StartsWith($s + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

# ------------------------------------------------ установленные программы ----

# Три ветки Uninstall: 64-битная, 32-битная и пользовательская. Одна и та же программа
# может лежать в двух - дубликаты снимаем по DisplayName.
function Get-InstalledPrograms {
    $keys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    $out = New-Object System.Collections.ArrayList
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($k in $keys) {
        $items = @()
        try { $items = Get-ItemProperty -Path $k -ErrorAction SilentlyContinue } catch { }
        foreach ($it in $items) {
            $name = $null
            try { $name = $it.DisplayName } catch { }
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            if (-not $seen.Add($name)) { continue }
            $loc = $null; $pub = $null
            try { $loc = $it.InstallLocation } catch { }
            try { $pub = $it.Publisher } catch { }
            [void] $out.Add([pscustomobject]@{
                Name      = $name
                Publisher = $pub
                Location  = $loc
            })
        }
    }
    return $out
}

# Ключ для сравнения имён: только буквы и цифры в нижнем регистре ("JetBrains Rider" -> jetbrainsrider).
function Get-NameKey([string] $s) {
    if (-not $s) { return '' }
    return ([regex]::Replace($s, '[^A-Za-z0-9]', '')).ToLowerInvariant()
}

# ------------------------------------------------- покрытие каталогом .cs ----

# Литералы из исходников: Path.Combine(<переменная>, "хвост") и абсолютные пути "C:\...".
# Значение переменной подставляем по таблице; неизвестные переменные пропускаем - их путь
# всё равно вычисляется в рантайме (steam из реестра, профили Chromium и т.п.).
function Get-CoveredPaths([string] $dir) {
    $vars = @{
        'lad'         = $env:LOCALAPPDATA
        'ad'          = $env:APPDATA
        'up'          = $env:USERPROFILE
        'pd'          = $env:ProgramData
        'temp'        = $env:TEMP
        '_winDir'     = $env:WinDir
        'sysDrive'    = (Split-Path -Qualifier $env:WinDir) + '\'
        'pf'          = $env:ProgramFiles
        'explorerDir' = (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Explorer')
    }
    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $fam = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $files = @()
    try { $files = Get-ChildItem -Path (Join-Path $dir '*.cs') -File -ErrorAction SilentlyContinue } catch { }
    foreach ($f in $files) {
        $text = ''
        try { $text = [System.IO.File]::ReadAllText($f.FullName) } catch { continue }

        foreach ($m in [regex]::Matches($text, 'Path\.Combine\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,\s*"((?:[^"\\]|\\.)*)"\s*\)')) {
            $v = $m.Groups[1].Value
            if (-not $vars.ContainsKey($v)) { continue }
            $base = $vars[$v]
            if (-not $base) { continue }
            $tail = $m.Groups[2].Value -replace '\\\\', '\'
            try { [void] $set.Add((Join-Path $base $tail).TrimEnd('\')) } catch { }
        }
        # Голая переменная как цель (AddDir(shell, lad, true, "IconCache.db", 0)) покрытием НЕ считается:
        # у такой цели есть маска и нет рекурсии, а как корень она помечала бы "covered" весь %LOCALAPPDATA%.

        # Часть каталога задана массивами имён приложений (Path.Combine(ad, msg[i])), поэтому
        # литералов Path.Combine для них в коде нет. Собираем такие имена отдельным списком:
        # это не сама цель, а «семья» - папка приложения, внутри которой цели создаёт помощник.
        foreach ($a in [regex]::Matches($text, 'new\s+string\[\]\s*\{(.*?)\}\s*;', [Text.RegularExpressions.RegexOptions]::Singleline)) {
            foreach ($s in [regex]::Matches($a.Groups[1].Value, '"((?:[^"\\]|\\.)*)"')) {
                $lit = $s.Groups[1].Value -replace '\\\\', '\'
                if ($lit.Length -lt 3 -or $lit.Contains('|') -or $lit.Contains('*') -or $lit.Contains(':')) { continue }
                foreach ($r in @($env:LOCALAPPDATA, $env:APPDATA, $env:ProgramData)) {
                    try { [void] $fam.Add((Join-Path $r $lit).TrimEnd('\')) } catch { }
                }
            }
        }
    }
    return @{ Exact = $set; Family = $fam }
}

# Имена, которые внутри папки приложения добавляют общие помощники каталога
# (AddElectronCache, AddChromium, AddJetBrains, AddSubdirCaches). Всё остальное внутри
# такой папки помощник не трогает, поэтому это «partial», а не «yes».
$helperLeaves = @(
    'cache', 'caches', 'code cache', 'gpucache', 'dawncache', 'dawngraphitecache', 'dawnwebgpucache',
    'grshadercache', 'shadercache', 'graphitedawncache', 'component_crx_cache', 'extensions_crx_cache',
    'cachestorage', 'scriptcache', 'media cache', 'application cache', 'cacheddata',
    'cachedextensionvsixs', 'cachedprofilesdata', 'logs', 'log', 'tmp', 'temp', 'crashes', 'reports',
    'cache2', 'startupcache', 'shader-cache', 'thumbnails', 'safebrowsing', 'minidumps', 'webcache'
)

# Покрыто = путь совпал с литералом каталога или лежит под ним. Плюс два обхода, которых
# в виде литералов нет вовсе: EBWebView у любого приложения на WebView2 и Saved\{Crashes,Logs}
# у любой игры на Unreal Engine - их каталог разбирает целиком.
function Get-Coverage([string] $path, $cov) {
    $p = $path.TrimEnd('\')
    $leaf = (Split-Path -Leaf $p).ToLowerInvariant()
    foreach ($c in $cov.Exact) {
        if ($p -eq $c) { return 'yes' }
        if ($p.StartsWith($c + '\', [StringComparison]::OrdinalIgnoreCase)) { return 'yes' }
    }
    if ($p -match '(?i)\\EBWebView\\' -and $helperLeaves -contains $leaf) { return 'yes' }
    if ($p -match '(?i)\\Saved\\(Crashes|Logs)$') { return 'yes' }
    foreach ($c in $cov.Family) {
        if ($p -eq $c) { return 'partial' }
        if ($p.StartsWith($c + '\', [StringComparison]::OrdinalIgnoreCase)) {
            if ($helperLeaves -contains $leaf) { return 'yes' }
            return 'partial'
        }
    }
    foreach ($c in $cov.Exact) {
        if ($c.StartsWith($p + '\', [StringComparison]::OrdinalIgnoreCase)) { return 'partial' }
    }
    return 'no'
}

# ------------------------------------------------------------ обход диска ----

function Get-DirSize([string] $path) {
    $sum = 0L
    $n = 0
    try {
        Get-ChildItem -LiteralPath $path -Recurse -Force -File -ErrorAction SilentlyContinue |
            ForEach-Object { $sum += $_.Length; $n++ }
    } catch { }
    return @($sum, $n)
}

# Обход в ширину с ограничением глубины. Найденная папка-кэш дальше не раскрывается:
# «X\Cache\Sub Cache» интересен только как часть «X\Cache».
function Find-CacheDirs($root, $rootName) {
    $found = New-Object System.Collections.ArrayList
    if (-not $root -or -not (Test-Path -LiteralPath $root)) { return $found }
    $queue = New-Object System.Collections.Queue
    $queue.Enqueue(@{ Path = $root; Level = 0 })
    while ($queue.Count -gt 0) {
        $cur = $queue.Dequeue()
        $kids = @()
        try { $kids = Get-ChildItem -LiteralPath $cur.Path -Directory -Force -ErrorAction SilentlyContinue } catch { continue }
        foreach ($d in $kids) {
            # За симлинком/junction лежит чужая папка: и считать её размер, и предлагать
            # к удалению нельзя - сам движок такие цели тоже пропускает.
            if (($d.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            $rel = $d.FullName.Substring($root.Length).TrimStart('\')
            if (Test-Skipped $rel) { continue }
            if (Test-CacheName $d.Name) {
                [void] $found.Add([pscustomobject]@{ Root = $rootName; RootPath = $root; Rel = $rel; Path = $d.FullName })
                continue
            }
            if ($cur.Level + 1 -lt $Depth) { $queue.Enqueue(@{ Path = $d.FullName; Level = $cur.Level + 1 }) }
        }
    }
    return $found
}

# ------------------------------------------------------------------ работа ----

Write-Host "SysDeck - app cache scanner (read-only)"
Write-Host ("src: {0}" -f $SrcDir)

$programs = Get-InstalledPrograms
Write-Host ("installed programs: {0}" -f $programs.Count)

$covered = Get-CoveredPaths $SrcDir
Write-Host ("catalog paths parsed from src: {0} exact, {1} app folders" -f $covered.Exact.Count, $covered.Family.Count)

$hits = New-Object System.Collections.ArrayList
foreach ($r in $roots) {
    Write-Host ("scanning {0} ..." -f $r.Path)
    foreach ($h in (Find-CacheDirs $r.Path $r.Name)) { [void] $hits.Add($h) }
}
Write-Host ("cache-like folders: {0}" -f $hits.Count)

# Индекс имён программ - по одному ключу на слово и на имя целиком, иначе «Discord» не
# найдётся в «Discord Inc.», а «PostgreSQL 16» - в папке «pgAdmin».
$progIndex = @{}
foreach ($p in $programs) {
    $k = Get-NameKey $p.Name
    if ($k.Length -ge 3 -and -not $progIndex.ContainsKey($k)) { $progIndex[$k] = $p.Name }
}

function Find-App([string] $vendor, [string] $app) {
    foreach ($cand in @($vendor, $app)) {
        if (-not $cand) { continue }
        $k = Get-NameKey $cand
        if ($k.Length -lt 3) { continue }
        if ($progIndex.ContainsKey($k)) { return $progIndex[$k] }
        foreach ($pk in $progIndex.Keys) {
            if ($pk.Length -ge 4 -and ($pk.Contains($k) -or $k.Contains($pk))) { return $progIndex[$pk] }
        }
    }
    return ''
}

$rows = New-Object System.Collections.ArrayList
$i = 0
foreach ($h in $hits) {
    $i++
    if ($i % 25 -eq 0) { Write-Host ("  measured {0}/{1}" -f $i, $hits.Count) }
    $sz = Get-DirSize $h.Path
    $mb = [Math]::Round($sz[0] / 1MB, 1)
    if ($mb -lt $MinMB) { continue }
    $parts = $h.Rel.Split('\')
    $vendor = $parts[0]
    $app = if ($parts.Length -gt 1) { $parts[1] } else { '' }
    [void] $rows.Add([pscustomobject]@{
        MB       = $mb
        Files    = $sz[1]
        Root     = $h.Root
        Rel      = $h.Rel
        Path     = $h.Path
        App      = (Find-App $vendor $app)
        Covered  = (Get-Coverage $h.Path $covered)
    })
}

$rows = @($rows | Sort-Object -Property MB -Descending)
if ($rows.Count -gt $Top) { $rows = $rows[0..($Top - 1)] }

# ------------------------------------------------------------------ отчёт ----

# Готовая строка каталога: путь пересобирается через ту же переменную, что и в BuildCleanCategories.
function New-AddDirLine($row) {
    $tail = $row.Rel -replace '\\', '\\'
    return ('AddDir(apps, Path.Combine({0}, "{1}"), true);   // {2} MB' -f $row.Root, $tail, $row.MB)
}

$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path -LiteralPath $dir)) { [void] (New-Item -ItemType Directory -Path $dir -Force) }

$sb = New-Object System.Text.StringBuilder
[void] $sb.AppendLine('# App cache scan')
[void] $sb.AppendLine('')
[void] $sb.AppendLine(('- machine: {0}, user: {1}' -f $env:COMPUTERNAME, $env:USERNAME))
[void] $sb.AppendLine(('- date: {0}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm')))
[void] $sb.AppendLine(('- roots: %LOCALAPPDATA% (lad), %APPDATA% (ad), %ProgramData% (pd); depth {0}, min {1} MB' -f $Depth, $MinMB))
[void] $sb.AppendLine(('- installed programs: {0}; catalog paths parsed from {1}: {2} exact + {3} app folders' -f $programs.Count, $SrcDir, $covered.Exact.Count, $covered.Family.Count))
[void] $sb.AppendLine(('- cache-like folders found: {0}, above the size threshold: {1}' -f $hits.Count, $rows.Count))
[void] $sb.AppendLine('- read-only scan: this script writes nothing but this file.')
[void] $sb.AppendLine('')
$sumNo = 0.0
foreach ($r in $rows) { if ($r.Covered -eq 'no') { $sumNo += $r.MB } }
[void] $sb.AppendLine(('Uncovered total: {0} MB' -f [Math]::Round($sumNo, 1)))
[void] $sb.AppendLine('')
[void] $sb.AppendLine('## Findings')
[void] $sb.AppendLine('')
[void] $sb.AppendLine('| MB | files | covered | app | path |')
[void] $sb.AppendLine('|---:|------:|---------|-----|------|')
foreach ($r in $rows) {
    [void] $sb.AppendLine(('| {0} | {1} | {2} | {3} | `%{4}%\{5}` |' -f $r.MB, $r.Files, $r.Covered,
        $(if ($r.App) { $r.App } else { '-' }),
        $(switch ($r.Root) { 'lad' { 'LOCALAPPDATA' } 'ad' { 'APPDATA' } default { 'ProgramData' } }),
        $r.Rel))
}
[void] $sb.AppendLine('')
[void] $sb.AppendLine('## Ready-to-paste catalog lines (uncovered only)')
[void] $sb.AppendLine('')
[void] $sb.AppendLine('Review every line before pasting: the scanner only knows that a folder is *named* like a cache.')
[void] $sb.AppendLine('Anything that may hold user data (projects, saves, tokens, unsynced documents) must not be added.')
[void] $sb.AppendLine('')
[void] $sb.AppendLine('```csharp')
foreach ($r in $rows) { if ($r.Covered -eq 'no') { [void] $sb.AppendLine((New-AddDirLine $r)) } }
[void] $sb.AppendLine('```')
[void] $sb.AppendLine('')
[void] $sb.AppendLine('## Partially covered (a deeper path is already in the catalog)')
[void] $sb.AppendLine('')
foreach ($r in $rows) { if ($r.Covered -eq 'partial') { [void] $sb.AppendLine(('- `{0}` ({1} MB)' -f $r.Path, $r.MB)) } }
[void] $sb.AppendLine('')

[System.IO.File]::WriteAllText($OutFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding $true))
Write-Host ("report: {0}" -f $OutFile)
