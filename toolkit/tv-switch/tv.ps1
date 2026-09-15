# tv.ps1 — включение/выключение телевизора на HDMI средствами Windows (CCD API через модуль DisplayConfig).
# Использование:  tv.ps1 -On | -Off | -Status -Name <часть имени> [-HwId <часть кода оборудования>] [-Watch 15]
# Выключенный через -Off дисплей остаётся в кабеле, но Windows его не «ведёт»: нет перестроек рабочего стола,
# нет пересборки цветов, GameCenter/NVIDIA App не реагируют на каждое подключение.
param(
    [switch]$On,
    [switch]$Off,
    [switch]$Status,
    [string]$Name = '',
    [string]$HwId = '',
    [int]$Watch = 15
)
$ErrorActionPreference = 'Stop'
# Пустой образец совпал бы с любым дисплеем, и -Off выключил бы первый попавшийся монитор.
if (-not $Name -and -not $HwId) {
    Write-Host 'Не задано имя телевизора: укажите его на странице «Скрипты» и нажмите «Сохранить».' -ForegroundColor Red
    exit 2
}
try { Import-Module DisplayConfig } catch {
    Write-Host 'Модуль DisplayConfig не найден. Установка: Install-Module DisplayConfig -Scope CurrentUser' -ForegroundColor Red
    exit 2
}

function Test-Tv($displayName, $path) {
    ($Name -and "$displayName" -match [regex]::Escape($Name)) -or ($HwId -and "$path" -match [regex]::Escape($HwId))
}
function Find-Tv {
    Get-DisplayInfo | Where-Object { Test-Tv $_.DisplayName $_.DevicePath } | Select-Object -First 1
}
function Show-Tv($tv) {
    if ($tv) {
        $state = 'ВЫКЛЮЧЕН в Windows'
        if ($tv.Active) { $state = 'ВКЛЮЧЁН, ' + $tv.Mode + ' позиция ' + $tv.Position }
        Write-Host ('Телевизор ' + $tv.DisplayName + ' (DisplayId ' + $tv.DisplayId + '): ' + $state)
    } else {
        Write-Host 'Телевизор Windows не видит: кабель не подключён или телевизор в глубоком сне.'
    }
}
function Watch-Flaps($secs) {
    # После смены HDMI-входа у телевизора появляется новый PnP-узел; старый остаётся в системе как отсутствующий.
    # Берём присутствующий, иначе самый свежий по дате появления.
    $dev = Get-PnpDevice -Class Monitor -ErrorAction SilentlyContinue |
        Where-Object { Test-Tv $_.FriendlyName $_.InstanceId } |
        Sort-Object -Property @{Expression={$_.Present}; Descending=$true}, @{Expression={(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_LastArrivalDate' -ErrorAction SilentlyContinue).Data}; Descending=$true} |
        Select-Object -First 1
    if (-not $dev) { Write-Host 'PnP-узла монитора нет.'; return }
    $seen = @{}
    $end = (Get-Date).AddSeconds($secs)
    while ((Get-Date) -lt $end) {
        $a = (Get-PnpDeviceProperty -InstanceId $dev.InstanceId -KeyName 'DEVPKEY_Device_LastArrivalDate' -ErrorAction SilentlyContinue).Data
        if ($a) { $seen[$a.ToString('HH:mm:ss')] = 1 }
        Start-Sleep -Milliseconds 1500
    }
    $n = $seen.Count
    $msg = 'Переподключений HDMI за ' + $secs + ' с: ' + ($n - 1)
    if ($n -le 1) { Write-Host ($msg + ' — линк стабилен.') -ForegroundColor Green }
    else { Write-Host ($msg + ' — линк дёргается, отметки: ' + (($seen.Keys | Sort-Object) -join ' ')) -ForegroundColor Yellow }
}

$tv = Find-Tv
if ($On) {
    if (-not $tv) { Show-Tv $null; exit 1 }
    if ($tv.Active) { Write-Host 'Уже включён.'; Show-Tv $tv; exit 0 }
    Enable-Display -DisplayId $tv.DisplayId
    Start-Sleep -Seconds 2
    Show-Tv (Find-Tv)
    exit 0
}
if ($Off) {
    if (-not $tv) { Show-Tv $null; exit 0 }
    if (-not $tv.Active) { Write-Host 'Уже выключен.'; Show-Tv $tv; exit 0 }
    Disable-Display -DisplayId $tv.DisplayId
    Start-Sleep -Seconds 2
    Show-Tv (Find-Tv)
    exit 0
}
# -Status (или без ключей): состояние + наблюдение за линком
Show-Tv $tv
Watch-Flaps $Watch
