// OTRAS COPIAS DE CMCOORDINATOR EN LAS CARPETAS DE COMPLEMENTOS DE REVIT.
//
// Revit carga todo manifiesto .addin de ProgramData, Program Files (2027) y el AppData de quien abre Revit. Dos con el
// mismo identificador dan «ID de complemento duplicado». Aquí se buscan por contenido, no por nombre: el identificador
// actual, el antiguo (5f96a79f…, heredado del OpenProject Revit Add-In y compartido con «Cmproyectosbim for Revit») y
// que sea nuestro (fabricante Cmcoordinator o Cmproyectosbim, o carga OpenProject.Revit.dll). Lo nuestro se mueve con su
// carpeta a {commonappdata}\Cmcoordinator\copias\<fecha>\<año>\; lo de otro fabricante no se toca.

const
  IdActual = 'b3ca797e-98df-4670-92e9-7b8d19c5c695';
  IdAntiguo = '5f96a79f-0e28-4d02-be10-251c8032a270';

var
  CopiasStamp: string;
  OtrosFabricantes: string;

// Las carpetas de complementos de un año: la común, la de Program Files y la de cada usuario del equipo.
function CarpetasDeComplementos(const RevitYear: string): TArrayOfString;
var
  Usuarios: string;
  Rec: TFindRec;
  N: Integer;
begin
  SetArrayLength(Result, 2);
  Result[0] := ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\') + RevitYear;
  Result[1] := ExpandConstant('{commonpf64}\Autodesk\Revit\Addins\') + RevitYear;
  Usuarios := ExtractFileDir(GetEnv('USERPROFILE'));
  if (Usuarios <> '') and FindFirst(Usuarios + '\*', Rec) then
  try
    repeat
      if ((Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Rec.Name <> '.') and (Rec.Name <> '..') and
         DirExists(Usuarios + '\' + Rec.Name + '\AppData\Roaming\Autodesk\Revit\Addins\' + RevitYear) then
      begin
        N := GetArrayLength(Result);
        SetArrayLength(Result, N + 1);
        Result[N] := Usuarios + '\' + Rec.Name + '\AppData\Roaming\Autodesk\Revit\Addins\' + RevitYear;
      end;
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

function ContenidoEnMinusculas(const Archivo: string): string;
var
  Texto: AnsiString;
begin
  Result := '';
  if LoadStringFromFile(Archivo, Texto) then
    Result := Lowercase(String(Texto));
end;

function TieneNuestroId(const Contenido: string): Boolean;
begin
  Result := (Pos(IdActual, Contenido) > 0) or (Pos(IdAntiguo, Contenido) > 0);
end;

function EsNuestro(const Contenido: string): Boolean;
begin
  Result := TieneNuestroId(Contenido) and
    ((Pos('<vendorid>cmcoordinator</vendorid>', Contenido) > 0) or (Pos('<vendorid>cmproyectosbim</vendorid>', Contenido) > 0) or
     (Pos('openproject.revit.dll', Contenido) > 0));
end;

// La carpeta del complemento que carga el manifiesto, solo si está dentro de la carpeta de complementos (nunca fuera).
function CarpetaDelManifiesto(const Carpeta, Contenido: string): string;
var
  Inicio, Fin: Integer;
  Ensamblado, Primero: string;
begin
  Result := '';
  Inicio := Pos('<assembly>', Contenido);
  Fin := Pos('</assembly>', Contenido);
  if (Inicio = 0) or (Fin <= Inicio) then exit;
  Ensamblado := Trim(Copy(Contenido, Inicio + 10, Fin - Inicio - 10));
  StringChangeEx(Ensamblado, '/', '\', True);
  if (Pos(':', Ensamblado) = 0) and (Copy(Ensamblado, 1, 2) <> '\\') then
  begin
    Primero := Ensamblado;
    if Pos('\', Primero) > 0 then
      Primero := Copy(Primero, 1, Pos('\', Primero) - 1)
    else
      exit;
    if (Primero <> '') and (Primero <> '..') and DirExists(Carpeta + '\' + Primero) then
      Result := Carpeta + '\' + Primero;
  end
  else if Pos(Lowercase(Carpeta) + '\', Lowercase(ExtractFileDir(Ensamblado))) = 1 then
    Result := ExtractFileDir(Ensamblado);
end;

// Lo nuestro de un año: rutas de manifiestos. Lo ajeno con nuestro identificador se anota en OtrosFabricantes.
function CopiasDelAnio(const RevitYear: string): TArrayOfString;
var
  Carpetas: TArrayOfString;
  Rec: TFindRec;
  I, N: Integer;
  Contenido: string;
begin
  SetArrayLength(Result, 0);
  Carpetas := CarpetasDeComplementos(RevitYear);
  for I := 0 to GetArrayLength(Carpetas) - 1 do
    if FindFirst(Carpetas[I] + '\*.addin', Rec) then
    try
      repeat
        Contenido := ContenidoEnMinusculas(Carpetas[I] + '\' + Rec.Name);
        if EsNuestro(Contenido) then
        begin
          N := GetArrayLength(Result);
          SetArrayLength(Result, N + 1);
          Result[N] := Carpetas[I] + '\' + Rec.Name;
        end
        else if TieneNuestroId(Contenido) and (Pos(Carpetas[I] + '\' + Rec.Name, OtrosFabricantes) = 0) then
          OtrosFabricantes := OtrosFabricantes + Carpetas[I] + '\' + Rec.Name + #13#10;
      until not FindNext(Rec);
    finally
      FindClose(Rec);
    end;
end;

procedure AnotarCopias(var Summary: string; const RevitYear: string);
var
  Copias: TArrayOfString;
  I: Integer;
begin
  Copias := CopiasDelAnio(RevitYear);
  for I := 0 to GetArrayLength(Copias) - 1 do
    Summary := Summary + 'Revit ' + RevitYear + ': ' + Copias[I] + #13#10;
end;

function MoverA(const Origen, Destino: string): Boolean;
begin
  ForceDirectories(ExtractFileDir(Destino));
  Result := RenameFile(Origen, Destino);
  if not Result then
    Log('No se pudo mover ' + Origen + ' a ' + Destino);
end;

// Mueve lo nuestro de un año, con su carpeta, a la copia de seguridad. False si algo no se pudo mover.
function ApartarCopiasDelAnio(const RevitYear: string): Boolean;
var
  Copias: TArrayOfString;
  I: Integer;
  Destino, Carpeta: string;
begin
  Result := True;
  if CopiasStamp = '' then
    CopiasStamp := GetDateTimeString('yyyymmdd-hhnnss', #0, #0);
  Copias := CopiasDelAnio(RevitYear);
  for I := 0 to GetArrayLength(Copias) - 1 do
  begin
    Destino := ExpandConstant('{commonappdata}\Cmcoordinator\copias\') + CopiasStamp + '\' + RevitYear + '\' + IntToStr(I + 1);
    Carpeta := CarpetaDelManifiesto(ExtractFileDir(Copias[I]), ContenidoEnMinusculas(Copias[I]));
    Log('Apartando ' + Copias[I] + ' (carpeta: ' + Carpeta + ') a ' + Destino);
    if (Carpeta <> '') and DirExists(Carpeta) then
      Result := MoverA(Carpeta, Destino + '\' + ExtractFileName(Carpeta)) and Result;
    Result := MoverA(Copias[I], Destino + '\' + ExtractFileName(Copias[I])) and Result;
  end;
end;

// Al desinstalar: lo nuestro de todos los años, sin copia (es desinstalar).
procedure QuitarCopiasDeTodosLosAnios();
var
  Y, I: Integer;
  Copias: TArrayOfString;
  Carpeta: string;
begin
  for Y := 2019 to 2027 do
  begin
    Copias := CopiasDelAnio(IntToStr(Y));
    for I := 0 to GetArrayLength(Copias) - 1 do
    begin
      Carpeta := CarpetaDelManifiesto(ExtractFileDir(Copias[I]), ContenidoEnMinusculas(Copias[I]));
      if (Carpeta <> '') and DirExists(Carpeta) then
        DelTree(Carpeta, True, True, True);
      DeleteFile(Copias[I]);
      Log('Desinstalado ' + Copias[I]);
    end;
  end;
end;
