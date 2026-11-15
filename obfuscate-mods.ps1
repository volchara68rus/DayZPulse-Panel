# ------------------------------------------------------------
#  Сборка и обфускация мода DayZ Pulse.
#
#  Результат — один мод-фолдер @DayZPulse с двумя аддонами:
#      dist\mods\@DayZPulse\Addons\DayZPulseCore.pbo
#      dist\mods\@DayZPulse\Addons\DayZPulseAntiCheat.pbo
#
#  Шаги:
#    1) pboobf pack       : mod\DayZPulseCore / mod\DayZPulseAntiCheat -> *.pbo.clean
#    2) pboobf obfuscate  : *.pbo.clean -> *.pbo (обфусцированные)
#    3) pboobf verify     : проверка, что аддоны не конфликтуют по именам классов
#    4) -Deploy           : скопировать собранный @DayZPulse на сервер (путь из -ServerDir)
#
#  Запуск:
#    powershell -ExecutionPolicy Bypass -File obfuscate-mods.ps1
#    powershell -ExecutionPolicy Bypass -File obfuscate-mods.ps1 -Level 3 -Safe
#    powershell -ExecutionPolicy Bypass -File obfuscate-mods.ps1 -NoObfuscate   # только pack
#    powershell -ExecutionPolicy Bypass -File obfuscate-mods.ps1 -Deploy -ServerDir C:\Server
# ------------------------------------------------------------
param(
    [int]$Level = 2,
    [switch]$Safe,
    [switch]$NoScripts,
    [int]$Seed = 0x5EED,
    [switch]$NoObfuscate,
    [switch]$Deploy,
    [string]$ServerDir = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root      = $PSScriptRoot
$proj      = Join-Path $root 'tools\PboObfuscator\PboObfuscator.csproj'
$exe       = Join-Path $root 'tools\PboObfuscator\bin\Release\net8.0\pboobf.exe'
$srcDir    = Join-Path $root 'mod'
$modDir    = Join-Path $root 'dist\mods\@DayZPulse'
$addonsDir = Join-Path $modDir 'Addons'

# Исходники аддона -> имя PBO (префикс совпадает с именем папки исходников).
$addons = [ordered]@{
    'DayZPulseCore'      = 'DayZPulseCore'
    'DayZPulseAntiCheat' = 'DayZPulseAntiCheat'
}

# 1. Собрать инструмент, если его нет.
if (-not (Test-Path $exe)) {
    Write-Host "==> Сборка pboobf" -ForegroundColor Cyan
    & dotnet build $proj -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "Не удалось собрать pboobf" }
}

New-Item -ItemType Directory -Force $addonsDir | Out-Null

# 2. Собрать чистые PBO из исходников.
Write-Host "==> Сборка чистых PBO из mod\" -ForegroundColor Cyan
foreach ($name in $addons.Keys) {
    $src = Join-Path $srcDir $name
    if (-not (Test-Path $src)) { throw "Не найдены исходники аддона: $src" }
    $clean = Join-Path $addonsDir "$name.pbo.clean"
    & $exe pack $src $clean $addons[$name]
    if ($LASTEXITCODE -ne 0) { throw "Не удалось собрать $name.pbo" }
}

if (-not $NoObfuscate) {
    # 3. Обфусцировать чистые PBO.
    $flags = @('--level', "$Level", '--seed', "0x$('{0:X}' -f $Seed)")
    if ($Safe)      { $flags += '--safe' }
    if ($NoScripts) { $flags += '--no-scripts' }

    Write-Host "==> Обфускация (level=$Level, seed=0x$('{0:X}' -f $Seed)$(if($Safe){', safe'})$(if($NoScripts){', no-scripts'}))" -ForegroundColor Cyan
    $failures = 0
    foreach ($name in $addons.Keys) {
        $clean = Join-Path $addonsDir "$name.pbo.clean"
        $pbo   = Join-Path $addonsDir "$name.pbo"
        & $exe obfuscate $clean $pbo @flags
        if ($LASTEXITCODE -ne 0) { $failures++ }
    }
    if ($failures -gt 0) { throw "Обфускация завершилась с ошибкой ($failures файл(ов))" }

    # 4. Проверка: аддоны одного мода компилируются вместе — имена классов не должны совпадать.
    $pbos = Get-ChildItem $addonsDir -Filter *.pbo -File | Where-Object { $_.Name -notlike '*.clean' } |
        Select-Object -ExpandProperty FullName
    if ($pbos.Count -ge 2) {
        Write-Host "==> Проверка совместимости аддонов" -ForegroundColor Cyan
        & $exe verify @pbos
        if ($LASTEXITCODE -ne 0) { throw "Аддоны конфликтуют по именам классов — НЕ выкладывайте. Смените -Seed и повторите." }
    }
} else {
    Write-Host "==> Обфускация пропущена (-NoObfuscate)" -ForegroundColor Yellow
}

# meta.cpp в корень мод-фолдера (нужен для загрузки мода игрой).
$metaSrc = Join-Path $srcDir 'meta.cpp'
if (Test-Path $metaSrc) {
    Copy-Item $metaSrc (Join-Path $modDir 'meta.cpp') -Force
} else {
    @"
protocol = 1;
publishedid = 0;
name = "DayZ Pulse";
timestamp = 0;
"@ | Set-Content -Path (Join-Path $modDir 'meta.cpp') -Encoding ASCII
}

Write-Host "Готово: $modDir" -ForegroundColor Green
Get-ChildItem $addonsDir -Filter *.pbo -File | Where-Object { $_.Name -notlike '*.clean' } |
    Select-Object Name, Length

# 5. Опционально — скопировать собранный мод на сервер.
if ($Deploy) {
    if ([string]::IsNullOrWhiteSpace($ServerDir)) {
        throw "Укажите каталог сервера: -Deploy -ServerDir C:\Server"
    }

    $target = Join-Path $ServerDir '@DayZPulse'
    $targetAddons = Join-Path $target 'Addons'
    New-Item -ItemType Directory -Force $targetAddons | Out-Null

    Write-Host "==> Копирование в $target" -ForegroundColor Cyan
    Get-ChildItem $addonsDir -Filter *.pbo -File | Where-Object { $_.Name -notlike '*.clean' } |
        ForEach-Object { Copy-Item $_.FullName $targetAddons -Force }
    Copy-Item (Join-Path $modDir 'meta.cpp') $target -Force

    Write-Host "Развёрнуто в $target" -ForegroundColor Green
}

Write-Host "Готово." -ForegroundColor Green
