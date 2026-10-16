function GetInstallTargetForYear(const RevitYear: string): string;
var
  Index: Integer;
begin
  Result := GetDefaultInstallTargetForYear(RevitYear);
  Index := GetPathIndexForYear(RevitYear);
  if (Index >= 0) and Assigned(InstallPathPage) and InstallPathPage.Edits[Index].Visible then
    if Trim(InstallPathPage.Values[Index]) <> '' then
      Result := Trim(InstallPathPage.Values[Index]);
end;

function GetPluginTargetForYear(const RevitYear: string): string;
begin
  Result := GetInstallTargetForYear(RevitYear) + '\{#AddinName}';
end;

function GetBrowserTargetForYear(const RevitYear: string): string;
begin
  Result := GetPluginTargetForYear(RevitYear) + '\OpenProject.Browser';
end;

function GetSelectedInstallTargets(const NewLine: string): string;
begin
  Result := '';
#if Defined(HasRevit2019)
  if IsSelectedComponent('revit2019') then
    Result := Result + 'Revit 2019: ' + GetInstallTargetForYear('2019') + NewLine;
#endif
#if Defined(HasRevit2020)
  if IsSelectedComponent('revit2020') then
    Result := Result + 'Revit 2020: ' + GetInstallTargetForYear('2020') + NewLine;
#endif
#if Defined(HasRevit2021)
  if IsSelectedComponent('revit2021') then
    Result := Result + 'Revit 2021: ' + GetInstallTargetForYear('2021') + NewLine;
#endif
#if Defined(HasRevit2022)
  if IsSelectedComponent('revit2022') then
    Result := Result + 'Revit 2022: ' + GetInstallTargetForYear('2022') + NewLine;
#endif
#if Defined(HasRevit2024)
  if IsSelectedComponent('revit2024') then
    Result := Result + 'Revit 2024: ' + GetInstallTargetForYear('2024') + NewLine;
#endif
#if Defined(HasRevit2025)
  if IsSelectedComponent('revit2025') then
    Result := Result + 'Revit 2025: ' + GetInstallTargetForYear('2025') + NewLine;
#endif
#if Defined(HasRevit2026)
  if IsSelectedComponent('revit2026') then
    Result := Result + 'Revit 2026: ' + GetInstallTargetForYear('2026') + NewLine;
#endif
#if Defined(HasRevit2027)
  if IsSelectedComponent('revit2027') then
    Result := Result + 'Revit 2027: ' + GetInstallTargetForYear('2027') + NewLine;
#endif
end;

function ShellUnquote(const Value: string): string;
begin
  Result := RemoveQuotes(Value);
end;

function RunPreviousUninstaller(): Boolean;
var
  UninstallCmd: string;
  UninstallExe: string;
  ResultCode: Integer;
begin
  Result := True;
  UninstallCmd := GetPreviousUninstallString();
  if UninstallCmd = '' then
    exit;

  UninstallExe := ShellUnquote(UninstallCmd);
  if Pos('.exe', LowerCase(UninstallExe)) > 0 then
    UninstallExe := Copy(UninstallExe, 1, Pos('.exe', LowerCase(UninstallExe)) + 3);

  if not FileExists(UninstallExe) then
    exit;

  if MsgBox(
    'Se detectó una instalación previa del instalador.'#13#10#13#10 +
    '¿Quieres desinstalarla antes de continuar?',
    mbConfirmation,
    MB_YESNO) = IDYES then
  begin
    Log('Running previous uninstaller: ' + UninstallExe);
    if not Exec(UninstallExe, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      MsgBox('No se pudo ejecutar el desinstalador previo.', mbError, MB_OK);
      Result := False;
      exit;
    end;

    if ResultCode <> 0 then
    begin
      MsgBox('La desinstalación previa devolvió código ' + IntToStr(ResultCode) + '.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  if IsRevitRunning() then
  begin
    MsgBox(
      'Cierra Autodesk Revit antes de instalar el add-in.'#13#10#13#10 +
      'El instalador necesita reemplazar archivos bloqueados en la carpeta de Addins.',
      mbError,
      MB_OK);
    Result := False;
    exit;
  end;

  Result := RunPreviousUninstaller();
end;

procedure InitializeWizard();
begin
  ExistingInstallDetected := False;
  InstallPathPage := CreateInputDirPage(
    wpSelectComponents,
    'Rutas de instalación',
    'Configura la ruta de instalación',
    'Edita la ruta de cada versión seleccionada antes de continuar.',
    False,
    '');
#if Defined(HasRevit2019)
  Revit2019PathIndex := InstallPathPage.Add('Ruta Revit 2019');
  InstallPathPage.Values[Revit2019PathIndex] := GetDefaultInstallTargetForYear('2019');
#endif
#if Defined(HasRevit2020)
  Revit2020PathIndex := InstallPathPage.Add('Ruta Revit 2020');
  InstallPathPage.Values[Revit2020PathIndex] := GetDefaultInstallTargetForYear('2020');
#endif
#if Defined(HasRevit2021)
  Revit2021PathIndex := InstallPathPage.Add('Ruta Revit 2021');
  InstallPathPage.Values[Revit2021PathIndex] := GetDefaultInstallTargetForYear('2021');
#endif
#if Defined(HasRevit2022)
  Revit2022PathIndex := InstallPathPage.Add('Ruta Revit 2022');
  InstallPathPage.Values[Revit2022PathIndex] := GetDefaultInstallTargetForYear('2022');
#endif
#if Defined(HasRevit2024)
  Revit2024PathIndex := InstallPathPage.Add('Ruta Revit 2024');
  InstallPathPage.Values[Revit2024PathIndex] := GetDefaultInstallTargetForYear('2024');
#endif
#if Defined(HasRevit2025)
  Revit2025PathIndex := InstallPathPage.Add('Ruta Revit 2025');
  InstallPathPage.Values[Revit2025PathIndex] := GetDefaultInstallTargetForYear('2025');
#endif
#if Defined(HasRevit2026)
  Revit2026PathIndex := InstallPathPage.Add('Ruta Revit 2026');
  InstallPathPage.Values[Revit2026PathIndex] := GetDefaultInstallTargetForYear('2026');
#endif
#if Defined(HasRevit2027)
  Revit2027PathIndex := InstallPathPage.Add('Ruta Revit 2027');
  InstallPathPage.Values[Revit2027PathIndex] := GetDefaultInstallTargetForYear('2027');
#endif

  ExistingInstallPage := CreateOutputMsgMemoPage(
    InstallPathPage.ID,
    'Instalaciones detectadas',
    'Revisión de instalaciones existentes',
    'El instalador revisó las rutas de las versiones seleccionadas antes de copiar archivos.',
    '');
  ExistingInstallPage.RichEditViewer.Text := 'Selecciona primero las versiones de Revit que deseas instalar.';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;

  if Assigned(ExistingInstallPage) and (PageID = ExistingInstallPage.ID) then
    Result := not HasDetectedExistingInstalls();
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if CurPageID = wpSelectComponents then
  begin
    if WizardSelectedComponents(False) = '' then
    begin
      MsgBox('Selecciona al menos una versión de Revit para instalar.', mbError, MB_OK);
      Result := False;
      exit;
    end;

    UpdateInstallPathPageSelection();
    exit;
  end;

  if Assigned(InstallPathPage) and (CurPageID = InstallPathPage.ID) then
  begin
    DetectSelectedExistingInstalls();

    if HasDetectedExistingInstalls() then
    begin
      ExistingInstallPage.RichEditViewer.Text :=
        'Se detectaron instalaciones previas en las rutas configuradas para las versiones seleccionadas.' + #13#10#13#10 +
        ExistingInstallSummary + #13#10 +
        'En la siguiente pantalla el instalador las desinstalará antes de copiar la nueva versión.';
    end;
    exit;
  end;

  if Assigned(ExistingInstallPage) and (CurPageID = ExistingInstallPage.ID) then
  begin
    if MsgBox(
      'El instalador eliminará las instalaciones detectadas de las versiones seleccionadas antes de continuar.'#13#10#13#10 +
      '¿Deseas proceder?',
      mbConfirmation,
      MB_YESNO) <> IDYES then
    begin
      Result := False;
      exit;
    end;

    if not RemoveSelectedExistingInstalls() then
    begin
      MsgBox(
        'No se pudo limpiar completamente alguna instalación detectada.'#13#10 +
        'Corrige el problema y vuelve a intentarlo.',
        mbError,
        MB_OK);
      Result := False;
      exit;
    end;

    DetectSelectedExistingInstalls();
    if ExistingInstallSummary <> '' then
    begin
      MsgBox(
        'Todavía se detectan archivos previos después de intentar desinstalar.'#13#10 +
        'Cierra Revit y elimina manualmente esos archivos antes de continuar.',
        mbError,
        MB_OK);
      Result := False;
      exit;
    end;

    ExistingInstallPage.RichEditViewer.Text := 'La limpieza previa terminó correctamente. El instalador continuará con la copia de archivos.';
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  Summary: String;
  Targets: String;
begin
  Targets := GetSelectedInstallTargets(NewLine);
  Summary := MemoComponentsInfo;
  if Targets <> '' then
    Summary := Summary + NewLine + 'Rutas de instalación:' + NewLine + Targets;
  Summary := Summary + NewLine + 'El instalador copiará el add-in en las carpetas Autodesk de cada versión seleccionada.';
  Summary := Summary + NewLine + 'También revisará y limpiará instalaciones previas de las versiones seleccionadas.';
  Result := Summary;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    Log('Installing add-in base path: ' + WizardDirValue);
    Log('Selected components: ' + WizardSelectedComponents(False));
    Log('Cleaning previous installations for selected versions before copying files.');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if IsRevitRunning() then
    begin
      MsgBox('Cierra Autodesk Revit antes de desinstalar el add-in.', mbError, MB_OK);
      Abort;
    end;
  end;
end;






