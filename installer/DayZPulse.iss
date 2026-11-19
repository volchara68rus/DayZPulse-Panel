; ------------------------------------------------------------
;  DayZPulse — установщик (Inno Setup 6)
;  Сборка: installer\build-setup.ps1
;
;  Панель — один исполняемый файл (self-contained, .NET встроен).
;  Программа ставится в Program Files, а её данные (настройки, логи,
;  резервные копии, steamcmd) — в ProgramData\DayZPulse, куда путь
;  передаётся переменной окружения DAYZPULSE_HOME. Так служба
;  (LocalSystem) и значок в трее (пользователь) работают с одними
;  и теми же данными, а Program Files остаётся только для чтения.
;
;  Порт панели установщик не спрашивает: он задаётся на странице
;  создания владельца, после чего панель сама перезапускается.
; ------------------------------------------------------------

#ifndef AppVersion
  #define AppVersion "2.2.4"
#endif

#define AppName "DayZ Pulse"
#define AppShortName "DayZPulse"
#define AppExe "DayZPulse.exe"
#define ServiceName "DayZPulse"
#define DataFolder "DayZPulse"

[Setup]
; Уникальный идентификатор приложения (не менять между версиями).
AppId={{9F2C4A17-3B6D-4E88-A5C1-7D4E9F0B2C63}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=DayZ Pulse
DefaultDirName={autopf}\{#AppShortName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename={#AppShortName}Setup-{#AppVersion}
SetupIconFile=app.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Сообщаем Windows об изменении переменных окружения (DAYZPULSE_HOME).
ChangesEnvironment=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=DayZ Pulse
VersionInfoDescription={#AppName} - установщик
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
; Каталог данных, общий для службы и пользователя: пользователи могут писать.
; Внутри приложение создаёт подпапку data (настройки, логи, копии, steamcmd).
Name: "{commonappdata}\{#DataFolder}"; Permissions: users-modify
Name: "{commonappdata}\{#DataFolder}\data"; Permissions: users-modify

[Files]
; Публикация панели целиком (один exe + wwwroot).
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; При обновлении убираем остатки прежних сборок (dll, pdb, локализации).
Type: filesandordirs; Name: "{app}\cs"
Type: filesandordirs; Name: "{app}\de"
Type: filesandordirs; Name: "{app}\es"
Type: filesandordirs; Name: "{app}\fr"
Type: filesandordirs; Name: "{app}\it"
Type: filesandordirs; Name: "{app}\ja"
Type: filesandordirs; Name: "{app}\ko"
Type: filesandordirs; Name: "{app}\pl"
Type: filesandordirs; Name: "{app}\pt-BR"
Type: filesandordirs; Name: "{app}\ru"
Type: filesandordirs; Name: "{app}\tr"
Type: filesandordirs; Name: "{app}\zh-Hans"
Type: filesandordirs; Name: "{app}\zh-Hant"
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.pdb"

[Registry]
; Путь к данным панели. Читается приложением первым (AppPaths),
; поэтому служба, трей и ручной запуск используют один каталог.
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; \
  ValueType: string; ValueName: "DAYZPULSE_HOME"; ValueData: "{commonappdata}\{#DataFolder}"; \
  Flags: uninsdeletevalue

; Автозапуск значка в трее при входе пользователя: служба работает в сессии 0
; и не может показать значок сама, а без автозапуска трей появлялся бы только
; сразу после установки (и исчезал после перезагрузки). HKLM: установщик идёт
; от администратора, поэтому HKCU попал бы в чужой профиль.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "DayZPulse"; \
  ValueData: """{app}\{#AppExe}"" --tray --no-browser --data-dir=""{commonappdata}\{#DataFolder}"""; \
  Flags: uninsdeletevalue

[Icons]
; Ярлыки открывают панель через сам exe (команда open): порт читается из
; настроек, поэтому смена порта не ломает ссылки (жёсткий localhost:8730 после
; смены порта вёл бы в никуда).
Name: "{group}\Открыть панель {#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "open --data-dir=""{commonappdata}\{#DataFolder}"""; IconFilename: "{app}\{#AppExe}"
Name: "{group}\Удалить {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "open --data-dir=""{commonappdata}\{#DataFolder}"""; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Регистрируем службу Windows (автозапуск, LocalSystem) с явным каталогом данных.
Filename: "{app}\{#AppExe}"; Parameters: "install-service --data-dir=""{commonappdata}\{#DataFolder}"""; StatusMsg: "Регистрация службы DayZPulse..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Parameters: "start-service"; StatusMsg: "Запуск службы..."; Flags: runhidden waituntilterminated
; Значок в трее для текущего пользователя.
Filename: "{app}\{#AppExe}"; Parameters: "--tray --no-browser --data-dir=""{commonappdata}\{#DataFolder}"""; Description: "Запустить значок {#AppName} в трее"; Flags: nowait postinstall runasoriginaluser
; Открыть панель в браузере.
Filename: "{code:GetPanelUrl}"; Description: "Открыть панель в браузере"; Flags: shellexec nowait postinstall skipifsilent unchecked

[UninstallRun]
; Сначала закрываем трей, затем снимаем службу.
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "KillTray"
Filename: "{app}\{#AppExe}"; Parameters: "uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[UninstallDelete]
; Полностью убираем каталог данных панели: настройки, журналы, аудит, SteamCMD
; и обновлённую веб-часть. Иначе после удаления остаётся ProgramData\DayZPulse
; с файлами на сотни мегабайт.
;
; Каталоги dayzpulse внутри папок серверов DayZ не трогаем: там резервные
; копии, база игроков и логи сервера — их удаляет сам пользователь.
Type: filesandordirs; Name: "{commonappdata}\{#DataFolder}"

; В папке установки приложение создаёт подкаталог data, когда Program Files
; доступен только для чтения и данные ложатся рядом с программой. Без этого
; правила после удаления оставалась пустая папка {app}\data.
Type: filesandordirs; Name: "{app}\data"
Type: filesandordirs; Name: "{app}\wwwroot"

; Убираем и саму папку установки: если после снятия службы и файлов остаются
; служебные каталоги, Inno оставляет {app}, а вместе с ней — пустую папку
; в Program Files.
Type: filesandordirs; Name: "{app}"

[Code]
var
  PanelUrl: String;

// Читаем порт из настроек панели (ProgramData\DayZPulse\settings.json).
// При первой установке файла нет — панель слушает порт по умолчанию 8730.
function ReadPanelPort(): String;
var
  FileName: String;
  Raw: AnsiString;
  Content: String;
  At: Integer;
  P: Integer;
  Digits: String;
begin
  Result := '8730';
  FileName := ExpandConstant('{commonappdata}\DayZPulse\data\settings.json');
  if not FileExists(FileName) then
    Exit;

  if not LoadStringFromFile(FileName, Raw) then
    Exit;

  Content := String(Raw);
  // Ищем "port": <число> внутри блока "web".
  At := Pos('"web"', Content);
  if At = 0 then
    Exit;
  Content := Copy(Content, At, Length(Content));

  At := Pos('"port"', Content);
  if At = 0 then
    Exit;
  Content := Copy(Content, At + 6, Length(Content));

  Digits := '';
  for P := 1 to Length(Content) do
  begin
    if (Content[P] >= '0') and (Content[P] <= '9') then
      Digits := Digits + Content[P]
    else if Digits <> '' then
      Break;
  end;

  if Digits <> '' then
    Result := Digits;
end;

function GetPanelUrl(Param: String): String;
begin
  Result := 'http://127.0.0.1:' + PanelUrl;
end;

// Перед копированием файлов останавливаем и снимаем прежнюю службу и трей,
// иначе exe занят и не перезапишется при обновлении.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  OldExe: String;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);

  OldExe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(OldExe) then
    Exec(OldExe, 'uninstall-service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Result := '';
end;

procedure InitializeWizard();
begin
  PanelUrl := ReadPanelPort();
end;

// После установки проверяем, что служба зарегистрирована.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if (not Exec(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode))
       or (ResultCode <> 0) then
      MsgBox('Служба {#ServiceName} не зарегистрирована. Проверьте журнал установки.', mbError, MB_OK);
  end;
end;
