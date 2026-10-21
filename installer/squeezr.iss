; Squeezr Windows installer definition (feature 049).
; Not compiled by hand - scripts\build-installer.ps1 supplies every define below.
;   ISCC.exe /DSourceDir=... /DAppVersion=... /DVersionInfoVersion=... /DRid=...
;            /DOutputDir=... /DOutputBaseFilename=... installer\squeezr.iss

#ifndef SourceDir
  #error SourceDir is required: /DSourceDir=<published tree, e.g. artifacts\publish>
#endif
#ifndef AppVersion
  #error AppVersion is required: /DAppVersion=<version from Squeezr.Cli.csproj>
#endif
#ifndef VersionInfoVersion
  #error VersionInfoVersion is required: /DVersionInfoVersion=<numeric x.y.z, no pre-release suffix>
#endif
#ifndef Rid
  #error Rid is required: /DRid=win-x64 or /DRid=win-arm64
#endif
#ifndef OutputDir
  #error OutputDir is required: /DOutputDir=<artifacts\installer>
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename is required: /DOutputBaseFilename=<SqueezrSetup-x.y.z-rid>
#endif

#if Rid == "win-x64"
  #define Architectures "x64compatible"
#elif Rid == "win-arm64"
  #define Architectures "arm64"
#else
  #error Unsupported Rid: expected win-x64 or win-arm64
#endif

[Setup]
AppId={{9F3B7C21-5E4A-4D68-8B1F-2C7A6D0E5431}
AppName=Squeezr
AppVersion={#AppVersion}
AppPublisher=Squeezr
AppPublisherURL=http://REDACTED-LAN-HOST:3000/REDACTED-FORGE-NS/Squeezr
AppSupportURL=http://REDACTED-LAN-HOST:3000/REDACTED-FORGE-NS/Squeezr
VersionInfoVersion={#VersionInfoVersion}
VersionInfoProductName=Squeezr
VersionInfoDescription=Squeezr CLI and MCP code indexer
DefaultDirName={autopf}\Squeezr
DisableProgramGroupPage=yes
; A CLI has nothing to pin to the Start menu; the install directory is the only
; choice worth offering, so the directory page stays and the group page goes.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed={#Architectures}
ArchitecturesInstallIn64BitMode={#Architectures}
MinVersion=10.0.14393
UninstallDisplayName=Squeezr {#AppVersion}
UninstallDisplayIcon={app}\squeezr.exe
; A live `squeezr watch` / `serve --watch` holds the binary open; let the
; Restart Manager close the holders instead of failing the copy.
CloseApplications=yes
RestartApplications=no
ChangesEnvironment=yes
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "addtopath"; Description: "Add Squeezr to the PATH environment variable"; GroupDescription: "Environment:"

[Files]
; Recursive on purpose: Roslyn's MSBuildWorkspace runs out-of-proc and the
; BuildHost* folders published next to the binary are not embedded by the
; single-file bundle. Ship only squeezr.exe and semantic indexing dies at `index`.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Code]
const
  EnvSubKeyUser = 'Environment';
  EnvSubKeyMachine = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

function EnvRootKey: Integer;
begin
  if IsAdminInstallMode then
    Result := HKEY_LOCAL_MACHINE
  else
    Result := HKEY_CURRENT_USER;
end;

function EnvSubKey: String;
begin
  if IsAdminInstallMode then
    Result := EnvSubKeyMachine
  else
    Result := EnvSubKeyUser;
end;

function NormalizePathEntry(const Value: String): String;
begin
  Result := Trim(Value);
  while (Length(Result) > 3) and (Result[Length(Result)] = '\') do
    Result := Copy(Result, 1, Length(Result) - 1);
end;

procedure SplitPathValue(const Value: String; var Items: TArrayOfString);
var
  Rest, Item: String;
  Sep, Count: Integer;
begin
  SetArrayLength(Items, 0);
  Rest := Value;
  while Length(Rest) > 0 do
  begin
    Sep := Pos(';', Rest);
    if Sep > 0 then
    begin
      Item := Copy(Rest, 1, Sep - 1);
      Rest := Copy(Rest, Sep + 1, Length(Rest) - Sep);
    end
    else
    begin
      Item := Rest;
      Rest := '';
    end;
    Count := GetArrayLength(Items);
    SetArrayLength(Items, Count + 1);
    Items[Count] := Item;
  end;
end;

function PathHasEntry(const PathValue, Dir: String): Boolean;
var
  Items: TArrayOfString;
  Target: String;
  I: Integer;
begin
  Result := False;
  Target := NormalizePathEntry(Dir);
  SplitPathValue(PathValue, Items);
  for I := 0 to GetArrayLength(Items) - 1 do
    if CompareText(NormalizePathEntry(Items[I]), Target) = 0 then
    begin
      Result := True;
      exit;
    end;
end;

procedure AddDirToPath(const Dir: String);
var
  PathValue: String;
begin
  // Read-modify-write from code rather than [Registry] with {olddata}: that
  // idiom truncates at 8191 characters and can rewrite a REG_EXPAND_SZ PATH
  // as REG_SZ, killing every %VAR% reference inside it.
  if not RegQueryStringValue(EnvRootKey, EnvSubKey, 'Path', PathValue) then
    PathValue := '';
  if PathHasEntry(PathValue, Dir) then
    exit;
  if (Length(PathValue) > 0) and (PathValue[Length(PathValue)] <> ';') then
    PathValue := PathValue + ';';
  PathValue := PathValue + Dir;
  if not RegWriteExpandStringValue(EnvRootKey, EnvSubKey, 'Path', PathValue) then
    if not WizardSilent then
      MsgBox('Squeezr could not update the PATH environment variable.' + #13#10#13#10 +
             'Add this directory to PATH manually:' + #13#10 + Dir,
             mbError, MB_OK);
end;

procedure RemoveDirFromPath(const Dir: String);
var
  PathValue, Rebuilt, Target: String;
  Items: TArrayOfString;
  I: Integer;
  Removed: Boolean;
begin
  if not RegQueryStringValue(EnvRootKey, EnvSubKey, 'Path', PathValue) then
    exit;
  Target := NormalizePathEntry(Dir);
  SplitPathValue(PathValue, Items);
  Rebuilt := '';
  Removed := False;
  for I := 0 to GetArrayLength(Items) - 1 do
  begin
    if CompareText(NormalizePathEntry(Items[I]), Target) = 0 then
      Removed := True
    else
    begin
      if I > 0 then
        Rebuilt := Rebuilt + ';';
      Rebuilt := Rebuilt + Items[I];
    end;
  end;
  if Removed then
    RegWriteExpandStringValue(EnvRootKey, EnvSubKey, 'Path', Rebuilt);
end;

function StoreDir: String;
begin
  Result := GetEnv('SQUEEZR_HOME');
  if Result = '' then
    Result := AddBackslash(GetEnv('USERPROFILE')) + '.squeezr';
end;

function DockerPresent: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('cmd.exe', '/C where docker >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function EmbeddingModelPresent: Boolean;
begin
  Result := DirExists(AddBackslash(StoreDir) + 'models') or DirExists(ExpandConstant('{app}\Models'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddDirToPath(ExpandConstant('{app}'));
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Notes: String;
begin
  if CurPageID <> wpFinished then
    exit;

  Notes := '';
  if WizardIsTaskSelected('addtopath') then
    Notes := Notes + #13#10#13#10 + 'PATH was updated - open a NEW terminal before running squeezr.';
  if not DockerPresent then
    Notes := Notes + #13#10#13#10 + 'Docker was not found. Squeezr works without it (trigram and symbol layers), ' +
             'but semantic search needs Qdrant in Docker. Install Docker Desktop, then run "squeezr services up".';
  if not EmbeddingModelPresent then
    Notes := Notes + #13#10#13#10 + 'No embedding model found. Semantic search stays disabled until you fetch one ' +
             'with scripts\fetch-model.ps1 from the Squeezr repository.';
  if Notes = '' then
    exit;

  // The finished label is sized for two lines; the hints need the whole page.
  WizardForm.FinishedLabel.AutoSize := False;
  WizardForm.FinishedLabel.Height := WizardForm.FinishedPage.ClientHeight - WizardForm.FinishedLabel.Top - ScaleY(8);
  WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + Notes;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveDirFromPath(ExpandConstant('{app}'));

  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) and DirExists(StoreDir) then
    MsgBox('Squeezr was removed.' + #13#10#13#10 +
           'The central store was left untouched:' + #13#10 +
           StoreDir + #13#10#13#10 +
           'It holds your workspaces, embedding models and the managed infra compose file, so they survive a ' +
           'reinstall. To remove it deliberately: run "squeezr drop" per workspace and "squeezr services down" ' +
           'first, then delete the directory.',
           mbInformation, MB_OK);
end;
