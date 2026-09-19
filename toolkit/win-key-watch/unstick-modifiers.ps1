# Ручной сброс зависших клавиш — то, что делает вотчер автоматически, но по кнопке.
# Снимает ЛЮБУЮ зажатую клавишу, не только восемь модификаторов.
#
#   .\unstick-modifiers.ps1            снять всё зажатое
#   .\unstick-modifiers.ps1 -WhatIf    только показать, ничего не трогать
#
# -Elevated / -OutFile — служебные: так скрипт перезапускает сам себя с правами
# администратора, когда обычной инъекции не хватило (UIPI не пускает ввод в окно
# с более высокой целостностью), и возвращает отчёт в родительское окно.
param(
  [switch]$WhatIf,
  [switch]$Elevated,
  [string]$OutFile,
  [switch]$NoPause
)

$ErrorActionPreference = 'SilentlyContinue'
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Definition }

function Say([string]$text, [string]$color = 'Gray') {
  Write-Host $text -ForegroundColor $color
  if ($OutFile) { Add-Content -Path $OutFile -Value $text -Encoding UTF8 }
}

$sig = @"
[DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern uint MapVirtualKey(uint uCode, uint uMapType);
"@
$K = Add-Type -MemberDefinition $sig -Name KbU -Namespace DU -PassThru
$KEYUP = 0x0002; $EXTENDED = 0x0001

# Расширенные (E0) клавиши: без флага KEYEVENTF_EXTENDEDKEY отпускание уходит не туда.
$extendedVk = @(0xA1,0xA3,0xA5,0x5B,0x5C,0x5D,0x21,0x22,0x23,0x24,0x25,0x26,0x27,0x28,0x2D,0x2E,0x6F,0x0D)

$named = @{
  0xA0='LSHIFT'; 0xA1='RSHIFT'; 0xA2='LCTRL'; 0xA3='RCTRL'
  0xA4='LALT';   0xA5='RALT';   0x5B='LWIN';  0x5C='RWIN'; 0x5D='MENU'
  0x08='Backspace'; 0x09='Tab'; 0x0D='Enter'; 0x13='Pause'; 0x1B='Esc'; 0x20='Space'
  0x21='PgUp'; 0x22='PgDn'; 0x23='End'; 0x24='Home'
  0x25='Left'; 0x26='Up'; 0x27='Right'; 0x28='Down'
  0x2C='PrintScreen'; 0x2D='Insert'; 0x2E='Delete'
  0x70='F1'; 0x71='F2'; 0x72='F3'; 0x73='F4'; 0x74='F5'; 0x75='F6'
  0x76='F7'; 0x77='F8'; 0x78='F9'; 0x79='F10'; 0x7A='F11'; 0x7B='F12'
}
$modifiers = 0xA0,0xA1,0xA2,0xA3,0xA4,0xA5,0x5B,0x5C

function Get-KeyName([int]$vk) {
  if ($named.ContainsKey($vk)) { return $named[$vk] }
  if ($vk -ge 0x30 -and $vk -le 0x5A) { return [char]$vk }           # 0-9, A-Z
  if ($vk -ge 0x60 -and $vk -le 0x69) { return "Num$($vk - 0x60)" }
  return ('VK 0x{0:X2}' -f $vk)
}

# Кнопки мыши (0x01,0x02,0x04-0x06) живут в той же таблице — их не трогаем.
# 0x10/0x11/0x12 — обобщённые SHIFT/CTRL/ALT: это проекция левых и правых, не отдельные
# клавиши, иначе в отчёте каждый модификатор двоится.
$scan = @(0x03) + (0x07..0x0F) + (0x13..0xFE)
function Get-Held {
  $h = @()
  foreach ($vk in $scan) {
    if (($K::GetAsyncKeyState($vk) -band 0x8000) -ne 0) { $h += $vk }
  }
  return ,$h
}

function Send-KeyUp([int]$vk) {
  $sc = $K::MapVirtualKey([uint32]$vk, 0)
  $flags = $KEYUP
  if ($extendedVk -contains $vk) { $flags = $flags -bor $EXTENDED }
  $K::keybd_event([byte]$vk, [byte]$sc, $flags, [UIntPtr]::Zero)
}

# ------------------------------------------------------------------ вотчер

function Get-WatcherProc {
  Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -match '-File .*win-key-watch\.ps1' -and $_.ProcessId -ne $PID }
}

function Restart-Watcher([string]$why) {
  $vbs = Join-Path $root 'start-hidden.vbs'
  if (-not (Test-Path $vbs)) { Say "  вотчер: не нашёл start-hidden.vbs, пропускаю" 'DarkGray'; return }
  foreach ($p in (Get-WatcherProc)) { Stop-Process -Id $p.ProcessId -Force }
  Start-Sleep -Milliseconds 400
  Start-Process 'wscript.exe' -ArgumentList "`"$vbs`"" -WindowStyle Hidden
  Start-Sleep -Milliseconds 600
  $now = Get-WatcherProc
  if ($now) { Say "  вотчер перезапущен ($why), PID $($now.ProcessId)" 'Cyan' }
  else      { Say "  вотчер НЕ поднялся ($why) — запусти start-hidden.vbs вручную" 'Red' }
}

# Вотчер умеет сам блокировать клавишу, которую счёл «залипшей»: тогда она молчит,
# хотя в таблице состояний чисто. Свежая строка BLOCKED — единственный видимый признак.
function Test-RecentBlock {
  $log = Join-Path $root 'winkey.log'
  if (-not (Test-Path $log)) { return $false }
  $lines = Get-Content $log -Tail 80
  foreach ($l in $lines) {
    if ($l -match 'BLOCKED' -and $l -match '^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
      $t = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', $null)
      if (((Get-Date) - $t).TotalSeconds -lt 180) { return $true }
    }
  }
  return $false
}

# ------------------------------------------------------------------ работа

Say ("=== Сброс зависших клавиш — {0} ===" -f (Get-Date -Format 'HH:mm:ss')) 'White'
if ($Elevated) { Say '(с правами администратора)' 'DarkGray' }

$held = Get-Held
$hadHeld = $held.Count -gt 0
if ($held.Count -gt 0) {
  Say ("Зажаты: {0}" -f (($held | ForEach-Object { Get-KeyName $_ }) -join ', ')) 'Yellow'

  if ($WhatIf) { Say 'WhatIf — ничего не менял.' 'DarkGray' }
  else {
    # Два прохода: первый инжект может не пережить автоповтор физически замкнутой клавиши.
    for ($pass = 1; $pass -le 2; $pass++) {
      foreach ($vk in $held) { Send-KeyUp $vk }
      Start-Sleep -Milliseconds 250
      $held = Get-Held
      if ($held.Count -eq 0) { break }
    }
  }
}

if ($held.Count -eq 0 -and -not $WhatIf) {
  if ($hadHeld) { Say 'Снято, все клавиши отпущены.' 'Green' }
  else          { Say 'Зажатых клавиш нет.' 'Green' }
}
elseif ($held.Count -gt 0 -and -not $WhatIf) {
  $left = ($held | ForEach-Object { Get-KeyName $_ }) -join ', '

  $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
               IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  if (-not $isAdmin -and -not $Elevated) {
    Say ("Не снялось: {0} — пробую ещё раз с правами администратора..." -f $left) 'Yellow'
    $tmp = Join-Path $env:TEMP ('unstick-{0}.txt' -f [guid]::NewGuid().ToString('N'))
    $argv = @('-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File',
              "`"$($MyInvocation.MyCommand.Path)`"",'-Elevated','-NoPause','-OutFile',"`"$tmp`"")
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argv -Wait -WindowStyle Hidden
    if (Test-Path $tmp) {
      Get-Content $tmp -Encoding UTF8 | Where-Object { $_ -notmatch '^===' } |
        ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
      Remove-Item $tmp -Force
    }
    $held = Get-Held
    if ($held.Count -eq 0) { Say 'Снято (потребовались права администратора).' 'Green' }
  }

  if ($held.Count -gt 0) {
    $left = ($held | ForEach-Object { Get-KeyName $_ }) -join ', '
    Say ("НЕ снялось: {0}" -f $left) 'Red'
    $onlyMods = -not ($held | Where-Object { $modifiers -notcontains $_ })
    if ($onlyMods) { Say '  Модификатор держится против инжекта — это физически замкнутая клавиша.' 'Red' }
    else           { Say '  Клавиша шлёт автоповтор быстрее, чем приходит отпускание — залипла физически.' 'Red' }
    Say '  Вотчер умеет её заглушить: перезапускаю его.' 'DarkGray'
    Restart-Watcher 'клавиша не снялась'
  }
}

# ---------------------------------------------------- проверка самого вотчера

if (-not $WhatIf -and -not $Elevated) {
  $w = Get-WatcherProc
  if (-not $w) {
    Say 'Вотчер не запущен — поднимаю.' 'Yellow'
    Restart-Watcher 'был не запущен'
  }
  elseif (Test-RecentBlock) {
    Say 'Вотчер только что блокировал клавишу — снимаю блокировку перезапуском.' 'Yellow'
    Restart-Watcher 'свежий BLOCKED в логе'
  }
  else {
    Say ("Вотчер работает, PID {0}." -f $w.ProcessId) 'DarkGray'
  }
}

if (-not $NoPause) {
  Write-Host ''
  Write-Host 'Окно можно закрыть.' -ForegroundColor DarkGray
}
