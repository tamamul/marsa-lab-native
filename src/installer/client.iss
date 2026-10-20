#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{B6F1C2A4-5D7E-4C3A-9A10-6D2E8F4B7A02}
AppName=MARSA Lab Client
AppVersion={#AppVersion}
AppPublisher=SMK Ma'arif 9 Kebumen
DefaultDirName={autopf}\MarsaLab\Client
DisableProgramGroupPage=yes
DisableDirPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=..\out
OutputBaseFilename=MarsaLab-Client-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "..\..\dist\client\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MarsaLabClient"; ValueData: """{app}\MarsaLab.Client.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\MarsaLab.Client.exe"; Flags: nowait postinstall skipifsilent runasoriginaluser; Description: "Jalankan MARSA Lab Client sekarang"

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im MarsaLab.Client.exe"; Flags: runhidden

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  ServerPage: TInputQueryWizardPage;

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

function ConfigPath: String;
begin
  Result := ExpandConstant('{app}\appsettings.json');
end;

procedure InitializeWizard;
begin
  ServerPage := CreateInputQueryPage(wpWelcome, 'Koneksi ke server', 'Alamat server MARSA Lab',
  'Isi sesuai PC guru. Pakai Token client (bukan token admin) dari instalasi Guru.');
  ServerPage.Add('Alamat server:', False);
  ServerPage.Add('Token client:', False);
  ServerPage.Values[0] := ExpandConstant('{param:SERVER|http://192.168.0.10:5080}');
  ServerPage.Values[1] := ExpandConstant('{param:TOKEN|}');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var u: String;
begin
  Result := True;
  if CurPageID = ServerPage.ID then
  begin
    u := Lowercase(ServerPage.Values[0]);
    if not ((Pos('http://', u) = 1) or (Pos('https://', u) = 1)) or (Pos('"', u) > 0) or (Pos('\', u) > 0) then
    begin MsgBox('Alamat harus diawali http:// , contoh http://192.168.0.10:5080', mbError, MB_OK); Result := False; end
    else if (Length(ServerPage.Values[1]) < 24) or not IsAlnum(ServerPage.Values[1]) then
    begin MsgBox('Token minimal 24 karakter, hanya huruf dan angka.', mbError, MB_OK); Result := False; end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im MarsaLab.Client.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(ConfigPath,
      '{"ServerUrl":"' + ServerPage.Values[0] + '","Token":"' + ServerPage.Values[1] + '","ClientId":"","HeartbeatSeconds":5}',
      False);
end;
