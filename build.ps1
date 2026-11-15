# Сборка и публикация DayZPulse
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained,
    [switch]$SingleFile
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Push-Location $root

try {
    Write-Host "=== Сборка DayZPulse ($Configuration / $Runtime) ===" -ForegroundColor Cyan

    $args = @('publish', '-c', $Configuration, '-r', $Runtime, '-o', (Join-Path $root 'publish'))

    if ($SelfContained) {
        $args += @('--self-contained', 'true')
    } else {
        $args += @('--self-contained', 'false')
    }

    if ($SingleFile) {
        $args += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
    }

    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "Сборка завершилась с кодом $LASTEXITCODE"
    }

    $output = Join-Path $root 'publish'
    Write-Host ""
    Write-Host "Готово: $output" -ForegroundColor Green
    Write-Host "Для установки службы: запустите install-service.cmd от имени администратора." -ForegroundColor Yellow

    Get-ChildItem $output -Filter '*.exe' | Select-Object Name, Length
}
finally {
    Pop-Location
}
