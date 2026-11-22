[Setup]
AppId={{38BD063A-D465-48FF-96ED-C15196DC72B5}
AppName=Windows Intelligence
AppVersion=0.2.48
DefaultDirName={localappdata}\Windows Intelligence
CreateAppDir=no
Uninstallable=no
DisableWelcomePage=no
DisableDirPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.22621
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\release
OutputBaseFilename=WindowsIntelligence-Setup
Compression=none
WizardStyle=modern

[Files]
Source: "..\artifacts\release\WindowsIntelligence.msix"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "..\artifacts\release\WindowsIntelligence-Signing.cer"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "Install-MSIX.ps1"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Script, ErrorLog: String;
  Details: AnsiString;
begin
  if CurStep <> ssPostInstall then exit;
  ErrorLog := ExpandConstant('{tmp}\WindowsIntelligence-Install.log');
  Script := '"' + ExpandConstant('{tmp}\Install-MSIX.ps1') + '" -ErrorLog "' + ErrorLog + '"';
  if (not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + Script, ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, Code)) or (Code <> 0) then
  begin
    if LoadStringFromFile(ErrorLog, Details) then
      RaiseException('Windows Intelligence could not be installed.' + #13#10 + UTF8Decode(Details))
    else
      RaiseException('Windows Intelligence could not be installed. Check AppXDeployment-Server in Event Viewer.');
  end;
end;
