# ------------------------------------------------------------
#  Сборка установщика DayZ Pulse
#
#    powershell -ExecutionPolicy Bypass -File installer\build-setup.ps1
#    powershell -ExecutionPolicy Bypass -File installer\build-setup.ps1 -Version 2.0.1
#
#  Результат: dist\DayZPulseSetup-<версия>.exe
#
#  Шаги:
#    1) dotnet publish DayZPulse (self-contained, win-x64, один файл) -> dist\app
#    2) генерация значков (installer\make-icon.ps1), если их нет
#    3) компиляция installer\DayZPulse.iss (Inno Setup 6) -> dist\DayZPulseSetup-<версия>.exe
# ------------------------------------------------------------
param(
    [string]$Version = "2.0.0"
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# На этой машине IPv6 без рабочего маршрута: .NET иначе падает с
# «сеть недоступна» при скачивании runtime-пакетов с nuget.org.
if (-not $env:DOTNET_SYSTEM_NET_DISABLEIPV6) { $env:DOTNET_SYSTEM_NET_DISABLEIPV6 = '1' }

$root      = Split-Path $PSScriptRoot -Parent
$dist      = Join-Path $root 'dist'
$appDir    = Join-Path $dist 'app'
$project   = Join-Path $root 'DayZPulse.csproj'

if (-not (Test-Path $project)) { throw "Не найден проект: $project" }

# 1. Публикация панели (один exe + wwwroot).
Write-Host "==> Публикация панели (self-contained, win-x64)" -ForegroundColor Cyan
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
New-Item -ItemType Directory -Path $appDir -Force | Out-Null

$publishArgs = @(
    $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    # Всё в один exe. Внутреннее сжатие выключено — финальное сжатие
    # делает сам установщик (LZMA), так итоговый архив меньше.
    '-p:PublishSingleFile=true',
    '-p:EnableCompressionInSingleFile=false',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    '-o', $appDir
)

# Публикация из папки проекта: --data-dir/obj рядом не мешают.
& dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Публикация завершилась с кодом $LASTEXITCODE" }

foreach ($required in @('DayZPulse.exe', 'wwwroot\index.html')) {
    $path = Join-Path $appDir $required
    if (-not (Test-Path $path)) { throw "В папке публикации нет $required" }
}

$sizeMb = [Math]::Round(((Get-ChildItem $appDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "    Размер папки приложения: $sizeMb МБ" -ForegroundColor DarkGray

# 2. Значки (генерируются один раз).
$icon = Join-Path $PSScriptRoot 'app.ico'
if (-not (Test-Path $icon)) {
    Write-Host "==> Генерация значков" -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'make-icon.ps1')
    if ($LASTEXITCODE -ne 0) { throw "Не удалось создать значки" }
}

# 3. Компилятор Inno Setup.
$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { $_ -and (Test-Path $_) }

$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) {
    $found = Get-ChildItem "$env:LOCALAPPDATA\Programs" -Filter 'ISCC.exe' -Recurse -Depth 4 -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
    if ($found) { $iscc = $found }
}
if (-not $iscc) {
    throw "Не найден ISCC.exe (Inno Setup 6). Установите: winget install --id JRSoftware.InnoSetup -e"
}

Write-Host "==> Компиляция установщика" -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'DayZPulse.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC завершился с кодом $LASTEXITCODE" }

$setup = Join-Path $dist "DayZPulseSetup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Установщик не создан: $setup" }

$setupMb = [Math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ""
Write-Host "Готово: $setup ($setupMb МБ)" -ForegroundColor Green
