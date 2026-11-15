# Установщик DayZ Pulse (v2)

Собирает один `dist\DayZPulseSetup-<версия>.exe`, который ставит панель как
службу Windows плюс значок в трее. Панель — **один исполняемый файл**
(self-contained, .NET встраивается, на целевой машине ставить .NET не нужно).

## Что делает установщик

1. Копирует панель в `C:\Program Files\DayZPulse`.
2. Создаёт каталог данных `C:\ProgramData\DayZPulse` (доступ на запись пользователям).
3. Прописывает переменную окружения `DAYZPULSE_HOME=C:\ProgramData\DayZPulse`,
   чтобы служба (LocalSystem) и значок в трее (пользователь) использовали
   **одни и те же** настройки, логи и резервные копии. Program Files при этом
   остаётся только для чтения.
4. Регистрирует и запускает службу `DayZPulse` (автозапуск, `LocalSystem`).
5. Запускает значок в трее для текущего пользователя.
6. Создаёт ярлыки «Открыть панель DayZ Pulse» и «Удалить DayZ Pulse» в меню «Пуск»
   (и на рабочем столе, если выбрать при установке).

**Порт панели установщик не спрашивает.** Он задаётся на странице создания
владельца (`/setup.html`) при первом открытии: панель стартует на порту по
умолчанию `8730`, а после сохранения владельца автоматически перезапускается
на выбранном порту. Ярлыки «Открыть панель» вызывают `DayZPulse.exe open`,
который читает актуальный порт из настроек, поэтому смена порта ярлыки не ломает.

Каталог данных (`C:\ProgramData\DayZPulse`) при удалении **не** удаляется.

## Сборка

Требуется:

- .NET SDK 10;
- Inno Setup 6 — `winget install --id JRSoftware.InnoSetup -e`.

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-setup.ps1
# или с другой версией:
powershell -ExecutionPolicy Bypass -File installer\build-setup.ps1 -Version 2.0.1
```

Скрипт:

1. публикует панель в `dist\app` (self-contained, `win-x64`, single-file);
2. генерирует значки `installer\app.ico`, `wwwroot\assets\favicon.ico`,
   `wwwroot\assets\logo.png`, `installer\tray.png` (если их ещё нет);
3. компилирует `installer\DayZPulse.iss`;
4. кладёт готовый установщик в `dist\DayZPulseSetup-<версия>.exe`.

## Установка

Обычная: запустить `dist\DayZPulseSetup-2.0.0.exe` и пройти мастер
(нужны права администратора).

Тихая установка:

```powershell
Start-Process -FilePath dist\DayZPulseSetup-2.0.0.exe -Verb RunAs -Wait `
  -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
```

После установки откройте панель (значок в трее или ярлык) и создайте владельца.

## Удаление

«Программы и компоненты» → `DayZ Pulse`, либо:

```powershell
& 'C:\Program Files\DayZPulse\unins000.exe'
```

Удаление останавливает и снимает службу, закрывает трей и удаляет файлы
программы. Каталог `C:\ProgramData\DayZPulse` остаётся на месте.

## Служба вручную

```cmd
sc.exe query DayZPulse
sc.exe stop DayZPulse
sc.exe start DayZPulse

:: перерегистрировать службу по текущему пути к exe
"C:\Program Files\DayZPulse\DayZPulse.exe" install-service --data-dir="C:\ProgramData\DayZPulse"
"C:\Program Files\DayZPulse\DayZPulse.exe" uninstall-service
```

## Значки

Все иконки делаются одним скриптом из исходника `icon.ico` (по умолчанию
`%USERPROFILE%\Downloads\icon.ico`), при этом **линия пульса перекрашивается
в градиент «красный → зелёный»**; серый «Z», силуэт карты и надпись не трогаются:

```powershell
powershell -ExecutionPolicy Bypass -File installer\make-icon.ps1
powershell -ExecutionPolicy Bypass -File installer\make-icon.ps1 -Source C:\path\icon.ico
```

На выходе:

- `installer\app.ico` — иконка `DayZPulse.exe` (задаётся в `DayZPulse.csproj`)
  и иконка установщика;
- `installer\dayz-pulse.ico` — копия для ярлыков;
- `wwwroot\assets\favicon.ico` — значок вкладки браузера;
- `wwwroot\assets\logo.png` — логотип панели;
- `installer\tray.png` — значок трея (эмблема без надписи).

## Разработка

Если панель запущена как служба, остановите её перед сборкой, иначе файлы
в `dist\app` могут быть заняты:

```cmd
sc.exe stop DayZPulse
```
