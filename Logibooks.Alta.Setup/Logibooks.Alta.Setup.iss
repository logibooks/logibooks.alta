#define EnglishStartShortcut "Start Alta adapter in console mode"
#define EnglishConfigShortcut "Open Alta adapter configuration"
#define RussianStartShortcut "Запустить адаптер Альта в консольном режиме"
#define RussianConfigShortcut "Открыть конфигурацию адаптера Альта"

[Setup]
AppId=Logibooks.Alta.Adapter
AppName=Logibooks Alta Adapter
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Logibooks Alta Adapter
AppPublisher=Logibooks
DefaultDirName={autopf}\Logibooks\Alta
DefaultGroupName=Logibooks
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousGroup=no
DisableReadyMemo=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousPrivileges=no
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
UninstallDisplayName={code:GetInstalledName}
UninstallDisplayIcon={app}\Logibooks.Alta.Adapter.exe
SetupIconFile=..\Logibooks.Alta.Service\Assets\Logibooks.Alta.Adapter.ico
OutputDir=Release
OutputBaseFilename=Logibooks.Alta.Adapter.Setup
Compression=lzma2
SolidCompression=yes
; The profile files and shortcuts are conditional on console mode.
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Messages]
english.PrivilegesRequiredOverrideInstruction=Choose how to run Logibooks Alta Adapter
english.PrivilegesRequiredOverrideText1=Choose a Windows service for this machine or a console app for your account.
english.PrivilegesRequiredOverrideText2=Choose a console app for your account or a Windows service for this machine.
english.PrivilegesRequiredOverrideCurrentUser=Install &console app for me only
english.PrivilegesRequiredOverrideCurrentUserRecommended=Install &console app for me only (no administrator rights)
english.PrivilegesRequiredOverrideAllUsers=Install Windows &service (administrator required)
english.PrivilegesRequiredOverrideAllUsersRecommended=Install Windows &service (administrator required)
russian.PrivilegesRequiredOverrideInstruction=Выберите режим работы адаптера Альта
russian.PrivilegesRequiredOverrideText1=Установите службу Windows для этого компьютера или консольное приложение для своей учётной записи.
russian.PrivilegesRequiredOverrideText2=Установите консольное приложение для своей учётной записи или службу Windows для этого компьютера.
russian.PrivilegesRequiredOverrideCurrentUser=Установить &консольное приложение только для меня
russian.PrivilegesRequiredOverrideCurrentUserRecommended=Установить &консольное приложение только для меня (права администратора не требуются)
russian.PrivilegesRequiredOverrideAllUsers=Установить &службу Windows (требуются права администратора)
russian.PrivilegesRequiredOverrideAllUsersRecommended=Установить &службу Windows (требуются права администратора)

[CustomMessages]
english.StartAltaShortcut={#EnglishStartShortcut}
english.OpenAltaConfigurationShortcut={#EnglishConfigShortcut}
russian.StartAltaShortcut={#RussianStartShortcut}
russian.OpenAltaConfigurationShortcut={#RussianConfigShortcut}

[Files]
Source: "{#PublishDir}\Logibooks.Alta.Adapter.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "Run-Alta.cmd"; DestDir: "{app}"; Flags: ignoreversion; Check: not IsAdminInstallMode
; Console configuration lives in the current user's profile and survives upgrades and uninstall.
Source: "..\Logibooks.Alta.Service\appsettings.template.json"; DestDir: "{localappdata}\Logibooks\Alta"; DestName: "appsettings.json"; Flags: onlyifdoesntexist uninsneveruninstall; Check: not IsAdminInstallMode

[Icons]
Name: "{group}\{cm:StartAltaShortcut}"; Filename: "{app}\Run-Alta.cmd"; WorkingDir: "{app}"; IconFilename: "{app}\Logibooks.Alta.Adapter.exe"; Check: not IsAdminInstallMode
Name: "{group}\{cm:OpenAltaConfigurationShortcut}"; Filename: "{sys}\notepad.exe"; Parameters: """{localappdata}\Logibooks\Alta\appsettings.json"""; Check: not IsAdminInstallMode

[InstallDelete]
; Remove the shortcut names used by console installers through v0.2.8.
Type: files; Name: "{userprograms}\Logibooks Alta Adapter\Logibooks Alta Adapter (Console).lnk"; Check: not IsAdminInstallMode
Type: files; Name: "{userprograms}\Logibooks Alta Adapter\Edit console configuration.lnk"; Check: not IsAdminInstallMode
Type: dirifempty; Name: "{userprograms}\Logibooks Alta Adapter"; Check: not IsAdminInstallMode
; Remove shortcuts left by an installation in the other language.
Type: files; Name: "{group}\{#EnglishStartShortcut}.lnk"; Languages: russian; Check: not IsAdminInstallMode
Type: files; Name: "{group}\{#EnglishConfigShortcut}.lnk"; Languages: russian; Check: not IsAdminInstallMode
Type: files; Name: "{group}\{#RussianStartShortcut}.lnk"; Languages: english; Check: not IsAdminInstallMode
Type: files; Name: "{group}\{#RussianConfigShortcut}.lnk"; Languages: english; Check: not IsAdminInstallMode

[Code]
const
  ServiceName = 'LogibooksAltaAdapter';
  ServiceKey = 'SYSTEM\CurrentControlSet\Services\' + ServiceName;
  ServiceDirectory = '{commonpf64}\Logibooks\Alta';
  LegacyUpgradeCode = '{36E2F8BB-D73C-47F2-A624-371518531952}';
  ErrorNoMoreItems = 259;

var
  ServiceExisted: Boolean;
  RestartPreviousService: Boolean;
  ServiceInstallFailed: Boolean;

function MsiEnumRelatedProducts(UpgradeCode: String; Reserved, Index: Cardinal;
  ProductCode: String): Cardinal;
  external 'MsiEnumRelatedProductsW@msi.dll stdcall delayload';

function SetEnvironmentVariable(Name, Value: String): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

function GetInstalledName(Param: String): String;
begin
  if IsAdminInstallMode then
    Result := 'Logibooks Alta Adapter (Service)'
  else
    Result := 'Logibooks Alta Adapter (Console)';
end;

function AdapterPath(): String;
begin
  Result := ExpandConstant('{app}\Logibooks.Alta.Adapter.exe');
end;

procedure InitializeWizard();
begin
  { A service executable must be installed in the protected Program Files folder. }
  if IsAdminInstallMode then
    WizardForm.DirEdit.Text := ExpandConstant(ServiceDirectory);
end;

function RunAdapter(Command: String): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if not Exec(AdapterPath(), Command, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := SysErrorMessage(ExitCode)
  else if ExitCode <> 0 then
    Result := Format('Alta service command %s failed with exit code %d.', [Command, ExitCode]);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ProductCode, ImagePath, StopCommand: String;
  LegacyStatus: Cardinal;
  ExitCode: Integer;
begin
  Result := '';
  if not IsAdminInstallMode then
    Exit;

  SetLength(ProductCode, 39);
  LegacyStatus := MsiEnumRelatedProducts(LegacyUpgradeCode, 0, 0, ProductCode);
  if LegacyStatus = 0 then
    Result := 'Uninstall the previous Logibooks Alta Adapter MSI in Installed apps, then run Setup again. Its configuration will be preserved.'
  else if LegacyStatus <> ErrorNoMoreItems then
    Result := Format('Could not check for a previous Alta MSI installation (Windows Installer code %d).', [LegacyStatus]);
  if Result <> '' then
    Exit;

  ServiceExisted := RegQueryStringValue(HKLM, ServiceKey, 'ImagePath', ImagePath);
  if not ServiceExisted then
    Exit;
  if CompareText(ImagePath, '"' + AdapterPath() + '"') <> 0 then
  begin
    Result := 'The existing Alta service uses a different executable path. Remove that service before running Setup.';
    Exit;
  end;

  StopCommand := '-NoProfile -NonInteractive -Command "$ErrorActionPreference = ''Stop''; ' +
    '$service = Get-Service -Name ''' + ServiceName + '''; ' +
    'if ($service.Status -eq ''Stopped'') { exit 2 }; ' +
    'if ($service.Status -ne ''StopPending'') { Stop-Service -InputObject $service -NoWait }; ' +
    '$service.WaitForStatus(''Stopped'', [TimeSpan]::FromSeconds(30)); exit 0"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
              StopCommand, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := 'Could not stop the Alta service. ' + SysErrorMessage(ExitCode)
  else if ExitCode = 0 then
    RestartPreviousService := True
  else if ExitCode <> 2 then
    Result := 'Could not stop the Alta service. Stop it in Services and retry.';
end;

function InstallService(): String;
begin
  if not SetEnvironmentVariable('LOGIBOOKS_ALTA_CONFIG_PATH',
      ExpandConstant('{commonappdata}\Logibooks\Alta\appsettings.json')) then
  begin
    Result := 'Could not set the service configuration path.';
    Exit;
  end;

  Result := RunAdapter('/Install');
  if (Result <> '') and not ServiceExisted then
    Log('Service cleanup after failed installation. ' + RunAdapter('/Uninstall'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Failure: String;
begin
  if (CurStep <> ssPostInstall) or not IsAdminInstallMode then
    Exit;

  { Register the service after the payload and its uninstaller are in place. }
  Failure := InstallService();
  ServiceInstallFailed := Failure <> '';
  if ServiceInstallFailed then
  begin
    Log(Failure);
    SuppressibleMsgBox(Failure, mbError, MB_OK, IDOK);
  end
  else
    RestartPreviousService := False;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = wpFinished) and ServiceInstallFailed;
end;

function GetCustomSetupExitCode(): Integer;
begin
  if ServiceInstallFailed then
    Result := 10
  else
    Result := 0;
end;

procedure DeinitializeSetup();
var
  ExitCode: Integer;
begin
  if RestartPreviousService then
    Exec(ExpandConstant('{sys}\sc.exe'), 'start ' + ServiceName, '', SW_HIDE,
         ewWaitUntilTerminated, ExitCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Failure, ImagePath: String;
begin
  if (CurUninstallStep = usUninstall) and IsAdminInstallMode and
     (CompareText(ExpandConstant('{app}'), ExpandConstant(ServiceDirectory)) = 0) and
     RegQueryStringValue(HKLM, ServiceKey, 'ImagePath', ImagePath) and
     (CompareText(ImagePath, '"' + AdapterPath() + '"') = 0) then
  begin
    Failure := RunAdapter('/Uninstall');
    if Failure <> '' then
    begin
      Log(Failure);
      SuppressibleMsgBox(Failure, mbError, MB_OK, IDOK);
      Abort;
    end;
  end;
end;
