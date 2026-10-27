var
  InstallPathPage: TInputDirWizardPage;
  ExistingInstallPage: TOutputMsgMemoWizardPage;
  ExistingInstallSummary: string;
  ExistingInstallDetected: Boolean;
#if Defined(HasRevit2019)
  Revit2019PathIndex: Integer;
#endif
#if Defined(HasRevit2020)
  Revit2020PathIndex: Integer;
#endif
#if Defined(HasRevit2021)
  Revit2021PathIndex: Integer;
#endif
#if Defined(HasRevit2022)
  Revit2022PathIndex: Integer;
#endif
#if Defined(HasRevit2023)
  Revit2023PathIndex: Integer;
#endif
#if Defined(HasRevit2024)
  Revit2024PathIndex: Integer;
#endif
#if Defined(HasRevit2025)
  Revit2025PathIndex: Integer;
#endif
#if Defined(HasRevit2026)
  Revit2026PathIndex: Integer;
#endif
#if Defined(HasRevit2027)
  Revit2027PathIndex: Integer;
#endif

function IsRevitRunning(): Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    Exec(
      ExpandConstant('{cmd}'),
      '/C tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and (ResultCode = 0);
end;

function GetPreviousUninstallString(): string;
begin
  Result := '';
  if not RegQueryStringValue(HKLM, '{#UninstallRegKey}', 'UninstallString', Result) then
    RegQueryStringValue(HKCU, '{#UninstallRegKey}', 'UninstallString', Result);
end;

function DirectoryExists(const Path: string): Boolean;
begin
  Result := DirExists(Path);
end;

function FileExistsEx(const Path: string): Boolean;
begin
  Result := FileExists(Path);
end;

function GetDefaultInstallTargetForYear(const RevitYear: string): string;
begin
  if RevitYear = '2027' then
    Result := ExpandConstant('{commonpf64}\Autodesk\Revit\Addins\' + RevitYear)
  else
    Result := ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\' + RevitYear);
end;

function GetPathIndexForYear(const RevitYear: string): Integer;
begin
  Result := -1;
#if Defined(HasRevit2019)
  if RevitYear = '2019' then Result := Revit2019PathIndex;
#endif
#if Defined(HasRevit2020)
  if RevitYear = '2020' then Result := Revit2020PathIndex;
#endif
#if Defined(HasRevit2021)
  if RevitYear = '2021' then Result := Revit2021PathIndex;
#endif
#if Defined(HasRevit2022)
  if RevitYear = '2022' then Result := Revit2022PathIndex;
#endif
#if Defined(HasRevit2023)
  if RevitYear = '2023' then Result := Revit2023PathIndex;
#endif
#if Defined(HasRevit2024)
  if RevitYear = '2024' then Result := Revit2024PathIndex;
#endif
#if Defined(HasRevit2025)
  if RevitYear = '2025' then Result := Revit2025PathIndex;
#endif
#if Defined(HasRevit2026)
  if RevitYear = '2026' then Result := Revit2026PathIndex;
#endif
#if Defined(HasRevit2027)
  if RevitYear = '2027' then Result := Revit2027PathIndex;
#endif
end;

procedure SetInstallPathEntryState(const RevitYear: string; const Selected: Boolean);
var
  Index: Integer;
begin
  Index := GetPathIndexForYear(RevitYear);
  if (Index < 0) or not Assigned(InstallPathPage) then
    exit;

  InstallPathPage.PromptLabels[Index].Visible := Selected;
  InstallPathPage.Edits[Index].Visible := Selected;
  InstallPathPage.Buttons[Index].Visible := Selected;

  if Selected and (Trim(InstallPathPage.Values[Index]) = '') then
    InstallPathPage.Values[Index] := GetDefaultInstallTargetForYear(RevitYear);
end;

function GetInstallTargetForYear(const RevitYear: string): string; forward;
function GetPluginTargetForYear(const RevitYear: string): string; forward;
function GetBrowserTargetForYear(const RevitYear: string): string; forward;
function IsSelectedComponent(const ComponentName: string): Boolean; forward;

procedure UpdateInstallPathPageSelection();
begin
#if Defined(HasRevit2019)
  SetInstallPathEntryState('2019', IsSelectedComponent('revit2019'));
#endif
#if Defined(HasRevit2020)
  SetInstallPathEntryState('2020', IsSelectedComponent('revit2020'));
#endif
#if Defined(HasRevit2021)
  SetInstallPathEntryState('2021', IsSelectedComponent('revit2021'));
#endif
#if Defined(HasRevit2022)
  SetInstallPathEntryState('2022', IsSelectedComponent('revit2022'));
#endif
#if Defined(HasRevit2023)
  SetInstallPathEntryState('2023', IsSelectedComponent('revit2023'));
#endif
#if Defined(HasRevit2024)
  SetInstallPathEntryState('2024', IsSelectedComponent('revit2024'));
#endif
#if Defined(HasRevit2025)
  SetInstallPathEntryState('2025', IsSelectedComponent('revit2025'));
#endif
#if Defined(HasRevit2026)
  SetInstallPathEntryState('2026', IsSelectedComponent('revit2026'));
#endif
#if Defined(HasRevit2027)
  SetInstallPathEntryState('2027', IsSelectedComponent('revit2027'));
#endif
end;

procedure AppendExistingInstall(var Summary: string; const RevitYear: string);
var
  InstallRoot: string;
  PluginDir: string;
  ManifestPath: string;
  AppDataManifest: string;
  AppDataDir: string;
begin
  InstallRoot := GetInstallTargetForYear(RevitYear);
  PluginDir := GetPluginTargetForYear(RevitYear);
  ManifestPath := InstallRoot + '\{#AddinName}.addin';
  AppDataManifest := ExpandConstant('{userappdata}\Autodesk\REVIT\Addins\' + RevitYear + '\{#AddinName}.addin');
  AppDataDir := ExpandConstant('{userappdata}\Autodesk\REVIT\Addins\' + RevitYear + '\{#AddinName}');

  if FileExistsEx(ManifestPath) or DirectoryExists(PluginDir) then
  begin
    Summary := Summary + 'Ruta configurada Revit ' + RevitYear + ': ' + PluginDir + #13#10;
  end;

  if FileExistsEx(AppDataManifest) or DirectoryExists(AppDataDir) then
    Summary := Summary + 'AppData Revit ' + RevitYear + ': ' + AppDataDir + #13#10;
end;

function IsSelectedComponent(const ComponentName: string): Boolean;
begin
  Result := WizardIsComponentSelected(ComponentName);
end;

procedure DetectSelectedExistingInstalls();
begin
  ExistingInstallSummary := '';
#if Defined(HasRevit2019)
  if IsSelectedComponent('revit2019') then
    AppendExistingInstall(ExistingInstallSummary, '2019');
#endif
#if Defined(HasRevit2020)
  if IsSelectedComponent('revit2020') then
    AppendExistingInstall(ExistingInstallSummary, '2020');
#endif
#if Defined(HasRevit2021)
  if IsSelectedComponent('revit2021') then
    AppendExistingInstall(ExistingInstallSummary, '2021');
#endif
#if Defined(HasRevit2022)
  if IsSelectedComponent('revit2022') then
    AppendExistingInstall(ExistingInstallSummary, '2022');
#endif
#if Defined(HasRevit2023)
  if IsSelectedComponent('revit2023') then
    AppendExistingInstall(ExistingInstallSummary, '2023');
#endif
#if Defined(HasRevit2024)
  if IsSelectedComponent('revit2024') then
    AppendExistingInstall(ExistingInstallSummary, '2024');
#endif
#if Defined(HasRevit2025)
  if IsSelectedComponent('revit2025') then
    AppendExistingInstall(ExistingInstallSummary, '2025');
#endif
#if Defined(HasRevit2027)
  if IsSelectedComponent('revit2027') then
    AppendExistingInstall(ExistingInstallSummary, '2027');
#endif
#if Defined(HasRevit2026)
  if IsSelectedComponent('revit2026') then
    AppendExistingInstall(ExistingInstallSummary, '2026');
#endif
  ExistingInstallDetected := ExistingInstallSummary <> '';
end;

function HasDetectedExistingInstalls(): Boolean;
begin
  Result := ExistingInstallDetected;
end;

function RemovePathWithCmd(const Path: string; const IsDirectory: Boolean): Boolean;
var
  ResultCode: Integer;
  Command: string;
begin
  if IsDirectory then
    Command := '/C if exist "' + Path + '" rmdir /S /Q "' + Path + '"'
  else
    Command := '/C if exist "' + Path + '" del /F /Q "' + Path + '"';

  Result := Exec(
    ExpandConstant('{cmd}'),
    Command,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);

  if not Result then
    Log('Failed to remove path: ' + Path + ' code=' + IntToStr(ResultCode));
end;

function HasExistingInstallForYear(const RevitYear: string): Boolean;
var
  InstallRoot: string;
  PluginDir: string;
  ManifestPath: string;
  AppDataManifest: string;
  AppDataDir: string;
begin
  InstallRoot := GetInstallTargetForYear(RevitYear);
  PluginDir := GetPluginTargetForYear(RevitYear);
  ManifestPath := InstallRoot + '\{#AddinName}.addin';
  AppDataManifest := ExpandConstant('{userappdata}\Autodesk\REVIT\Addins\' + RevitYear + '\{#AddinName}.addin');
  AppDataDir := ExpandConstant('{userappdata}\Autodesk\REVIT\Addins\' + RevitYear + '\{#AddinName}');

  Result :=
    FileExistsEx(ManifestPath) or
    DirectoryExists(PluginDir) or
    FileExistsEx(AppDataManifest) or
    DirectoryExists(AppDataDir);
end;

function RemoveExistingInstallForYear(const RevitYear: string): Boolean;
var
  InstallRoot: string;
  PluginDir: string;
  ManifestPath: string;
  AppDataManifest: string;
  AppDataDir: string;
begin
  InstallRoot := GetInstallTargetForYear(RevitYear);
  PluginDir := GetPluginTargetForYear(RevitYear);
  ManifestPath := InstallRoot + '\{#AddinName}.addin';
  AppDataManifest := ExpandConstant('{userappdata}\Autodesk\REVIT\Addins\' + RevitYear + '\{#AddinName}.addin');
  AppDataDir := ExpandConstant('{userappdata}\Autodesk\REVIT\Addins\' + RevitYear + '\{#AddinName}');

  Log('Removing existing install for Revit ' + RevitYear);
  Result :=
    RemovePathWithCmd(ManifestPath, False) and
    RemovePathWithCmd(PluginDir, True) and
    RemovePathWithCmd(AppDataManifest, False) and
    RemovePathWithCmd(AppDataDir, True);

  if Result then
    Result := not HasExistingInstallForYear(RevitYear);

  if not Result then
    Log('Existing install still detected after removal for Revit ' + RevitYear);
end;

function RemoveSelectedExistingInstalls(): Boolean;
begin
  Result := True;
#if Defined(HasRevit2019)
  if IsSelectedComponent('revit2019') and HasExistingInstallForYear('2019') then
    Result := RemoveExistingInstallForYear('2019') and Result;
#endif
#if Defined(HasRevit2020)
  if IsSelectedComponent('revit2020') and HasExistingInstallForYear('2020') then
    Result := RemoveExistingInstallForYear('2020') and Result;
#endif
#if Defined(HasRevit2021)
  if IsSelectedComponent('revit2021') and HasExistingInstallForYear('2021') then
    Result := RemoveExistingInstallForYear('2021') and Result;
#endif
#if Defined(HasRevit2022)
  if IsSelectedComponent('revit2022') and HasExistingInstallForYear('2022') then
    Result := RemoveExistingInstallForYear('2022') and Result;
#endif
#if Defined(HasRevit2023)
  if IsSelectedComponent('revit2023') and HasExistingInstallForYear('2023') then
    Result := RemoveExistingInstallForYear('2023') and Result;
#endif
#if Defined(HasRevit2024)
  if IsSelectedComponent('revit2024') and HasExistingInstallForYear('2024') then
    Result := RemoveExistingInstallForYear('2024') and Result;
#endif
#if Defined(HasRevit2025)
  if IsSelectedComponent('revit2025') and HasExistingInstallForYear('2025') then
    Result := RemoveExistingInstallForYear('2025') and Result;
#endif
#if Defined(HasRevit2027)
  if IsSelectedComponent('revit2027') and HasExistingInstallForYear('2027') then
    Result := RemoveExistingInstallForYear('2027') and Result;
#endif
#if Defined(HasRevit2026)
  if IsSelectedComponent('revit2026') and HasExistingInstallForYear('2026') then
    Result := RemoveExistingInstallForYear('2026') and Result;
#endif
end;

