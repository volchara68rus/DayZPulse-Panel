@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

echo === DayZPulse: установка службы Windows ===
echo.

net session >nul 2>&1
if errorlevel 1 (
    echo Требуются права администратора. Перезапустите файл от имени администратора.
    pause
    exit /b 1
)

if not exist "DayZPulse.exe" (
    echo Не найден DayZPulse.exe рядом со скриптом.
    echo Сначала опубликуйте приложение: dotnet publish -c Release
    pause
    exit /b 1
)

DayZPulse.exe install-service
if errorlevel 1 (
    echo Не удалось установить службу.
    pause
    exit /b 1
)

echo.
echo Запуск службы...
DayZPulse.exe start-service

echo.
DayZPulse.exe service-status
echo.
echo Готово. Панель будет доступна по адресу из settings.json (по умолчанию http://127.0.0.1:8730).
pause
