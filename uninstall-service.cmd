@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

echo === DayZPulse: удаление службы Windows ===
echo.

net session >nul 2>&1
if errorlevel 1 (
    echo Требуются права администратора. Перезапустите файл от имени администратора.
    pause
    exit /b 1
)

DayZPulse.exe stop-service
DayZPulse.exe uninstall-service
echo.
echo Служба удалена.
pause
