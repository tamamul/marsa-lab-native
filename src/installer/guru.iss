#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{B6F1C2A4-5D7E-4C3A-9A10-6D2E8F4B7A01}
AppName=MARSA Lab Guru
AppVersion={#AppVersion}
AppPublisher=SMK Ma'arif 9 Kebumen
DefaultDirName={autopf}\MarsaLab\Guru
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=..\out
OutputBaseFilename=MarsaLab-Guru-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\MarsaLab.Desktop.exe

[Tasks]
Name: "autostart"; Description: "Jalankan server otomatis saat Windows menyala (disarankan, agar client selalu terhubung)"

[Files]
Source: "..\..\dist\guru\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\dist\webview2\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not WebView2Installed
Source: "gen-token.ps1"; Flags: dontcopy

[Icons]
Name: "{group}\MARSA Lab"; Filename: "{app}\MarsaLab.Desktop.exe"
Name: "{autodesktop}\MARSA Lab"; Filename: "{app}\MarsaLab.Desktop.exe"

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MarsaLabServer"; ValueData: """{app}\MarsaLab.Admin.exe"" --no-browser"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""MARSA Lab Admin"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""MARSA Lab Admin"" dir=in action=allow protocol=TCP localport=5080 profile=private,domain program=""{app}\MarsaLab.Admin.exe"""; Flags: runhidden
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Memasang WebView2 Runtime..."; Check: not WebView2Installed
Filename: "{app}\MarsaLab.Desktop.exe"; Description: "Buka MARSA Lab"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im MarsaLab.Admin.exe"; Flags: runhidden
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im MarsaLab.Desktop.exe"; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""MARSA Lab Admin"""; Flags: runhidden

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  TokenPage: TInputQueryWizardPage;

function IsAlnum(const S: String): Boolean;
var i: Integer; c: Char;
begin
  Result := True;
  for i := 1 to Length(S) do
  begin
    c := S[i];
    if not (((c >= '0') and (c <= '9')) or ((c >= 'A') and (c <= 'Z')) or ((c >= 'a') and (c <= 'z'))) then
    begin Result := False; Exit; end;
  end;
end;

function WebView2Installed: Boolean;
var v: String;
begin
  Result :=
    (RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0'))
    or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0'));
end;

function ConfigPath: String;
begin
  Result := ExpandConstant('{app}\appsettings.json');
end;

function NewToken(const Name: String): String;
var rc: Integer; S: AnsiString;
begin
  Result := '';
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      ExpandConstant('-NoProfile -ExecutionPolicy Bypass -File "{tmp}\gen-token.ps1" -OutFile "{tmp}\' + Name + '"'),
      '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0) then
    if LoadStringFromFile(ExpandConstant('{tmp}\' + Name), S) then Result := Trim(String(S));
end;

procedure InitializeWizard;
begin
  ExtractTemporaryFile('gen-token.ps1');
  TokenPage := CreateInputQueryPage(wpSelectDir, 'Pengaturan server', 'Token dan kode kelas',
    'Token admin hanya untuk PC guru. Token client dipasang di semua PC lab. Catat keduanya di notepad atau lainya sebelum lanjut.');
  TokenPage.Add('Token admin (khusus PC guru, min. 24 huruf/angka):', False);
  TokenPage.Add('Kode kelas siswa (opsional, huruf/angka):', False);
  TokenPage.Add('Token client (untuk PC lab Client, min. 24 huruf/angka):', False);
  TokenPage.Values[0] := ExpandConstant('{param:TOKEN|' + NewToken('t1.txt') + '}');
  TokenPage.Values[1] := ExpandConstant('{param:CODE|}');
  TokenPage.Values[2] := ExpandConstant('{param:CTOKEN|' + NewToken('t2.txt') + '}');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // Saat upgrade, pertahankan konfigurasi yang sudah ada
  Result := (PageID = TokenPage.ID) and FileExists(ConfigPath);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = TokenPage.ID then
  begin
    if (Length(TokenPage.Values[0]) < 24) or not IsAlnum(TokenPage.Values[0]) then
    begin MsgBox('Token admin minimal 24 karakter, hanya huruf dan angka.', mbError, MB_OK); Result := False; end
    else if not IsAlnum(TokenPage.Values[1]) then
    begin MsgBox('Kode kelas hanya boleh huruf dan angka.', mbError, MB_OK); Result := False; end
    else if (Length(TokenPage.Values[2]) < 24) or not IsAlnum(TokenPage.Values[2]) or (TokenPage.Values[2] = TokenPage.Values[0]) then
    begin MsgBox('Token client minimal 24 karakter (huruf/angka) dan harus berbeda dari token admin.', mbError, MB_OK); Result := False; end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im MarsaLab.Admin.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im MarsaLab.Desktop.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not FileExists(ConfigPath) then
    SaveStringToFile(ConfigPath,
    '{"Lab":{"Port":5080,"Token":"' + TokenPage.Values[0] + '","ClientToken":"' + TokenPage.Values[2] + '","OfflineAfterSeconds":15,"StudentCode":"' + TokenPage.Values[1] + '","MaxUploadMB":50},"Logging":{"LogLevel":{"Default":"Information","Microsoft.AspNetCore":"Warning"}}}',
      False);
end;
