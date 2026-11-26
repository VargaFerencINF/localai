; EfMailAgent installer (Inno Setup 6). Built by build.ps1:
;   iscc /DAppVersion=<VERSION> /DDistDir=<repo>\dist /DRepoDir=<repo> installer\EfMailAgent.iss
; Unattended install (all wizard values can also be given on the command line):
;   EfMailAgent-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /LLMURL=http://127.0.0.1:11434 /LLMMODEL=qwen3.5:9b /OUTPUT=D:\csv

#ifndef RepoDir
  #define RepoDir AddBackslash(SourcePath) + ".."
#endif
#ifndef DistDir
  #define DistDir RepoDir + "\dist"
#endif
#ifndef AppVersion
  #define VersionFile FileOpen(RepoDir + "\VERSION")
  #define AppVersion Trim(FileRead(VersionFile))
  #expr FileClose(VersionFile)
#endif

[Setup]
AppId={{7E3C6A52-9C1B-4F4B-9E0A-3B5D2C8F1A71}
AppName=EfMailAgent
AppVersion={#AppVersion}
AppVerName=EfMailAgent {#AppVersion}
AppPublisher=Energofish
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\EfMailAgent
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#DistDir}
OutputBaseFilename=EfMailAgent-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\EfMailAgent.exe
UninstallDisplayName=EfMailAgent e-mail AI feldolgozó
CloseApplications=no
SetupLogging=yes

[Languages]
Name: "hu"; MessagesFile: "compiler:Languages\Hungarian.isl"

[Files]
Source: "{#DistDir}\EfMailAgent.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DistDir}\efmailagent.xpi"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoDir}\samples\rules\*"; DestDir: "{app}\samples\rules"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoDir}\samples\emails\*"; DestDir: "{app}\samples\emails"; Flags: ignoreversion recursesubdirs createallsubdirs
#if FileExists(RepoDir + "\docs\USER_GUIDE_HU.md")
Source: "{#RepoDir}\docs\USER_GUIDE_HU.md"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Dirs]
; The data folder survives uninstall unless the user explicitly asks to delete it.
Name: "{commonappdata}\EfMailAgent"; Flags: uninsneveruninstall

[Run]
Filename: "{app}\EfMailAgent.exe"; Parameters: "open-admin"; Description: "Az admin felület megnyitása a böngészőben"; Flags: postinstall nowait skipifsilent runhidden runasoriginaluser

[UninstallRun]
Filename: "{app}\EfMailAgent.exe"; Parameters: "uninstall-addon"; Flags: runhidden waituntilterminated; RunOnceId: "UninstallAddon"
Filename: "{app}\EfMailAgent.exe"; Parameters: "uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "UninstallService"

[Code]
var
  LlmPage: TInputQueryWizardPage;
  ProviderPage: TInputOptionWizardPage;
  OutputPage: TInputDirWizardPage;
  AccountPage: TInputQueryWizardPage;
  Upgrade: Boolean;
  ResultNotes: String;

function DataDir(): String;
begin
  Result := ExpandConstant('{commonappdata}\EfMailAgent');
end;

{ Quotes one command-line argument (CommandLineToArgvW rules: backslashes before a quote and at the end are doubled). }
function QuoteArg(Value: String): String;
var
  I, Slashes: Integer;
  C: Char;
begin
  Result := '"';
  Slashes := 0;
  for I := 1 to Length(Value) do
  begin
    C := Value[I];
    if C = '\' then
      Slashes := Slashes + 1
    else
    begin
      if C = '"' then
        Result := Result + StringOfChar('\', Slashes + 1);
      Slashes := 0;
    end;
    Result := Result + C;
  end;
  Result := Result + StringOfChar('\', Slashes) + '"';
end;

function RunTool(Params: String; var ResultCode: Integer): Boolean;
begin
  Log('EfMailAgent.exe ' + Params);
  Result := Exec(ExpandConstant('{app}\EfMailAgent.exe'), Params, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('  -> exit code ' + IntToStr(ResultCode));
end;

procedure InitializeWizard();
begin
  Upgrade := FileExists(DataDir() + '\appsettings.json');

  ProviderPage := CreateInputOptionPage(wpSelectDir,
    'Helyi AI (LLM) szerver', 'Milyen szerver futtatja a modellt?',
    'Az e-mailek tartalma nem hagyja el a szervert: a feldolgozást egy ezen a gépen (vagy a helyi hálózaton) futó modell végzi.',
    True, False);
  ProviderPage.Add('Ollama');
  ProviderPage.Add('OpenAI-kompatibilis szerver (LM Studio, llama.cpp)');
  if ExpandConstant('{param:LLMPROVIDER|ollama}') = 'openai_compatible' then
    ProviderPage.SelectedValueIndex := 1
  else
    ProviderPage.SelectedValueIndex := 0;

  LlmPage := CreateInputQueryPage(ProviderPage.ID,
    'Helyi AI (LLM) szerver', 'A szerver címe és a modell neve',
    'A modell később is megadható vagy módosítható az admin felület Beállítások oldalán.');
  LlmPage.Add('A szerver címe:', False);
  LlmPage.Add('A modell neve (pl. qwen3.5:9b; üresen hagyható):', False);
  LlmPage.Values[0] := ExpandConstant('{param:LLMURL|http://127.0.0.1:11434}');
  LlmPage.Values[1] := ExpandConstant('{param:LLMMODEL|}');

  OutputPage := CreateInputDirPage(LlmPage.ID,
    'CSV kimenet', 'Hová kerüljenek a CSV fájlok?',
    'A feldolgozott e-mailek sorai ebbe a mappába kerülnek. Hálózati megosztás is lehet, de akkor a szolgáltatás fiókjának írási joga kell legyen hozzá (lásd a következő oldalt).',
    False, '');
  OutputPage.Add('');
  OutputPage.Values[0] := ExpandConstant('{param:OUTPUT|' + DataDir() + '\output}');

  AccountPage := CreateInputQueryPage(OutputPage.ID,
    'Szolgáltatásfiók', 'Milyen fiókkal fusson az EfMailAgent szolgáltatás?',
    'Üresen hagyva a beépített LocalSystem fiókkal fut, ez a legtöbb esetben megfelelő.' + #13#10 + #13#10 +
    'Ha a CSV kimenet hálózati megosztáson van, adj meg egy tartományi fiókot (pl. CEG\efmailagent), amelynek írási joga van a megosztáshoz; a LocalSystem a hálózaton a számítógépfiókként jelenik meg.');
  AccountPage.Add('Felhasználó (üres = LocalSystem):', False);
  AccountPage.Add('Jelszó:', True);
  AccountPage.Values[0] := ExpandConstant('{param:ACCOUNT|}');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { An upgrade keeps every existing setting: the configuration pages are not shown. }
  Result := Upgrade and ((PageID = ProviderPage.ID) or (PageID = LlmPage.ID) or (PageID = OutputPage.ID) or (PageID = AccountPage.ID));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = LlmPage.ID) and (Pos('://', LlmPage.Values[0]) = 0) then
  begin
    MsgBox('Add meg a szerver teljes címét, pl. http://127.0.0.1:11434', mbError, MB_OK);
    Result := False;
  end;
  if (CurPageID = AccountPage.ID) and (Trim(AccountPage.Values[0]) <> '') and (AccountPage.Values[1] = '') then
  begin
    MsgBox('Add meg a szolgáltatásfiók jelszavát.', mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  { Upgrade in place: stop the service (the running job goes back to the queue) so the exe can be replaced. }
  if FileExists(ExpandConstant('{app}\EfMailAgent.exe')) then
  begin
    Log('Stopping the EfMailAgent service before the upgrade');
    Exec(ExpandConstant('{app}\EfMailAgent.exe'), 'stop-service', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Params, Provider: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  { 1. Data folder, token (first install only, kept on upgrade), sample rules (only into an empty rules folder), settings. }
  Params := 'configure';
  if not Upgrade then
  begin
    if ProviderPage.SelectedValueIndex = 1 then Provider := 'openai_compatible' else Provider := 'ollama';
    Params := Params + ' --llm-provider ' + Provider + ' --llm-base-url ' + QuoteArg(Trim(LlmPage.Values[0])) +
      ' --llm-model ' + QuoteArg(Trim(LlmPage.Values[1])) + ' --output ' + QuoteArg(Trim(OutputPage.Values[0]));
  end;
  if not RunTool(Params, ResultCode) or (ResultCode <> 0) then
    ResultNotes := ResultNotes + #13#10 + '- A beállítások mentése nem sikerült (' + IntToStr(ResultCode) + '). Ellenőrizd: ' + DataDir() + '\appsettings.json';

  { 2. Windows service (an upgrade keeps the service account). }
  Params := 'install-service';
  if (not Upgrade) and (Trim(AccountPage.Values[0]) <> '') then
    Params := Params + ' --account ' + QuoteArg(Trim(AccountPage.Values[0])) + ' --password ' + QuoteArg(AccountPage.Values[1]);
  if not RunTool(Params, ResultCode) or (ResultCode <> 0) then
    ResultNotes := ResultNotes + #13#10 + '- A szolgáltatás telepítése vagy indítása nem sikerült (' + IntToStr(ResultCode) + '). Részletek: ' + DataDir() + '\logs';

  { 3. Thunderbird add-on via policies.json. }
  if RunTool('install-addon', ResultCode) then
  begin
    if ResultCode = 6 then
      ResultNotes := ResultNotes + #13#10 + '- A Thunderbird nem található ezen a gépen. Telepítése után futtasd rendszergazdaként: "' + ExpandConstant('{app}') + '\EfMailAgent.exe" install-addon'
    else if ResultCode <> 0 then
      ResultNotes := ResultNotes + #13#10 + '- Az add-on telepítése nem sikerült (' + IntToStr(ResultCode) + '). Futtasd kézzel: EfMailAgent.exe install-addon';
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Text: String;
begin
  if CurPageID <> wpFinished then
    Exit;
  Text := 'Az EfMailAgent telepítve van, a szolgáltatás fut.' + #13#10 + #13#10 +
    'Admin felület: http://127.0.0.1:47811/admin' + #13#10 +
    '(a bejelentkezéshez szükséges token: "' + ExpandConstant('{app}') + '\EfMailAgent.exe" print-token)' + #13#10 + #13#10 +
    'A Thunderbird újraindítása szükséges, hogy az add-on települjön és megkapja a beállításait.';
  if ResultNotes <> '' then
    Text := Text + #13#10 + #13#10 + 'Figyelem:' + ResultNotes;
  WizardForm.FinishedLabel.Caption := Text;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  if not DirExists(DataDir()) then
    Exit;
  { Default (and silent) answer: keep the data. }
  if SuppressibleMsgBox('Törlöd az EfMailAgent adatait is?' + #13#10 + #13#10 + DataDir() + #13#10 +
      '(beállítások és token, szabályok és promptok, CSV kimenet, feldolgozási sor, naplók)' + #13#10 + #13#10 +
      'Ha később újratelepíted, a megtartott adatokkal folytatódik a működés.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    DelTree(DataDir(), True, True, True);
end;
