Guía del flujo local BIM/IFC: [Cmcoordinator](tools/local-live-viewer/GUIA_0003.md). Código actual: `D:\Cmcoordinator`.

# Guia De Instalacion Y Limpieza

## Objetivo
Documento operativo para instalar el add-in de Revit, evitar instalaciones duplicadas y verificar que Revit este cargando la copia correcta.

## Rutas Importantes
- Workspace del proyecto:
  `D:\Cmcoordinator`
- Instalacion por usuario (`AppData`):
  `C:\Users\Administrador\AppData\Roaming\Autodesk\REVIT\Addins\2026`
- Instalacion global (`ProgramData`):
  `C:\ProgramData\Autodesk\Revit\Addins\2026`
- Instalacion global Revit 2027 (`Program Files`):
  `C:\Program Files\Autodesk\Revit\Addins\2027`
- Logs del add-in:
  `C:\Users\Administrador\AppData\Roaming\Cmcoordinator\logs`

## Regla Operativa
- Mantener una sola instalacion activa.
- Preferir `ProgramData` como destino final de despliegue.
- Evitar tener `OpenProject.Revit` en `AppData` y `ProgramData` al mismo tiempo.
- Excepcion Revit 2027: el manifiesto global debe ir en `C:\Program Files\Autodesk\Revit\Addins\2027`.

## Antes De Compilar
Si Revit esta abierto, los DLL quedan bloqueados y Visual Studio no puede copiar archivos al folder de add-ins.

Verificar si Revit sigue abierto:

```powershell
Get-Process Revit -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,MainWindowTitle,StartTime
```

Si aparece un proceso, cerrar Revit antes de compilar.

## Compilacion
Compilar el proyecto para Revit 2026:

```powershell
dotnet build src/OpenProject.Revit/OpenProject.Revit.csproj -c Debug-2026 -p:Platform=x64
```

## Instalacion Limpia
### Revit 2027
Revit 2027 no carga manifiestos all-users desde `C:\ProgramData\Autodesk\Revit\Addins\2027`.

Debe quedar en:

```powershell
C:\Program Files\Autodesk\Revit\Addins\2027
```

Si el add-in esta en `ProgramData`, Revit 2027 lo ignora. El journal lo registra asi:

```text
Add-in manifest file from: C:\ProgramData\Autodesk\Revit\Addins\2027\Cmcoordinator.addin, won't be loaded.
All-users Add-in manifest files must be installed to: C:\Program Files\Autodesk\Revit\Addins\2027
```

### 1. Borrar instalaciones anteriores
Eliminar las copias antiguas en `AppData` y `ProgramData`:

```powershell
$targets = @(
  'C:\Users\Administrador\AppData\Roaming\Autodesk\REVIT\Addins\2026\Cmcoordinator',
  'C:\Users\Administrador\AppData\Roaming\Autodesk\REVIT\Addins\2026\Cmcoordinator.addin',
  'C:\ProgramData\Autodesk\Revit\Addins\2026\Cmcoordinator',
  'C:\ProgramData\Autodesk\Revit\Addins\2026\Cmcoordinator.addin',
  'C:\ProgramData\Autodesk\Revit\Addins\2026\Cmcoordinator.disabled',
  'C:\Users\Administrador\AppData\Roaming\Autodesk\REVIT\Addins\2027\Cmcoordinator',
  'C:\Users\Administrador\AppData\Roaming\Autodesk\REVIT\Addins\2027\Cmcoordinator.addin',
  'C:\ProgramData\Autodesk\Revit\Addins\2027\Cmcoordinator',
  'C:\ProgramData\Autodesk\Revit\Addins\2027\Cmcoordinator.addin',
  'C:\Program Files\Autodesk\Revit\Addins\2027\Cmcoordinator',
  'C:\Program Files\Autodesk\Revit\Addins\2027\Cmcoordinator.addin'
)

foreach ($path in $targets) {
  if (Test-Path -LiteralPath $path) {
    Remove-Item -LiteralPath $path -Recurse -Force
  }
}
```

### 2. Instalar una sola copia en `ProgramData`

```powershell
$srcRoot = 'D:\Cmcoordinator'
$dstRoot = 'C:\ProgramData\Autodesk\Revit\Addins\2026'
$dstPlugin = Join-Path $dstRoot 'Cmcoordinator'
$dstBrowser = Join-Path $dstPlugin 'OpenProject.Browser'

New-Item -ItemType Directory -Force -Path $dstPlugin | Out-Null
New-Item -ItemType Directory -Force -Path $dstBrowser | Out-Null

Copy-Item -Force "$srcRoot\src\OpenProject.Revit\Cmcoordinator.addin" "$dstRoot\Cmcoordinator.addin"
Copy-Item -Recurse -Force "$srcRoot\src\OpenProject.Revit\bin\x64\Debug-2026\net8.0-windows\*" $dstPlugin
Copy-Item -Recurse -Force "$srcRoot\src\OpenProject.Browser\bin\x64\Debug\net48\*" $dstBrowser

$dupManifest = Join-Path $dstPlugin 'Cmcoordinator.addin'
if (Test-Path -LiteralPath $dupManifest) {
  Remove-Item -LiteralPath $dupManifest -Force
}
```

### 3. Instalar Revit 2027 en `Program Files`

```powershell
$srcRoot = 'D:\Cmcoordinator'
$dstRoot = 'C:\Program Files\Autodesk\Revit\Addins\2027'
$dstPlugin = Join-Path $dstRoot 'Cmcoordinator'
$dstBrowser = Join-Path $dstPlugin 'OpenProject.Browser'

New-Item -ItemType Directory -Force -Path $dstPlugin | Out-Null
New-Item -ItemType Directory -Force -Path $dstBrowser | Out-Null

Copy-Item -Force "$srcRoot\src\OpenProject.Revit\Cmcoordinator.addin" "$dstRoot\Cmcoordinator.addin"
Copy-Item -Recurse -Force "$srcRoot\src\OpenProject.Revit\bin\x64\Debug-2027\net10.0-windows7.0\*" $dstPlugin
Copy-Item -Recurse -Force "$srcRoot\src\OpenProject.Browser\bin\x64\Debug\net48\*" $dstBrowser

$dupManifest = Join-Path $dstPlugin 'Cmcoordinator.addin'
if (Test-Path -LiteralPath $dupManifest) {
  Remove-Item -LiteralPath $dupManifest -Force
}
```

## Verificacion De Duplicados
### Verificar que no exista copia en `AppData`

```powershell
Get-ChildItem C:\Users\Administrador\AppData\Roaming\Autodesk\REVIT\Addins\2026 -Recurse -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -like 'OpenProject.Revit*' }
```

Resultado esperado: ningun archivo ni carpeta de `OpenProject.Revit`.

### Verificar instalacion unica en `ProgramData`

```powershell
(Get-ChildItem C:\ProgramData\Autodesk\Revit\Addins\2026 -Recurse -Filter Cmcoordinator.addin).Count
(Get-ChildItem C:\ProgramData\Autodesk\Revit\Addins\2026 -Recurse -Filter OpenProject.Browser.exe).Count
```

Resultado esperado:
- `Cmcoordinator.addin`: `1`
- `OpenProject.Browser.exe`: `1`

### Verificar que Revit cargo la copia correcta
Revisar el `build stamp` en el log de Revit:

```powershell
Select-String -Path C:\Users\Administrador\AppData\Roaming\Cmcoordinator\logs\OpenProject.Revit.Log.*.txt -Pattern 'build stamp'
```

Resultado esperado:
- La ruta debe apuntar a `C:\ProgramData\Autodesk\Revit\Addins\2026\Cmcoordinator\OpenProject.Revit.dll`
- No debe apuntar a `%AppData%\Autodesk\REVIT\Addins\2026`

## Verificacion De Logs UI
Para revisar eventos del selector de proyectos:

```powershell
Select-String -Path C:\Users\Administrador\AppData\Roaming\Cmcoordinator\logs\OpenProject.Revit.Log.*.txt `
  -Pattern 'ProjectDropDownOpened|ProjectSelectionChanged|ProjectDropDownClosed|SearchTextBoxKeyDown'
```

## Problemas Frecuentes
### Error de copia bloqueada por Revit
Sintoma:
- `The process cannot access the file ... because it is being used by another process`
- `El archivo se ha bloqueado por: Autodesk Revit`

Solucion:
1. Cerrar Revit.
2. Compilar otra vez.
3. Reinstalar.

### El buscador o la UI no cambia aunque ya compilaste
Causa tipica:
- Revit sigue cargando una copia vieja desde `AppData`.

Solucion:
1. Hacer limpieza completa.
2. Instalar solo en `ProgramData`.
3. Verificar `build stamp` en logs.

### Manifiesto duplicado
Caso:
- Existe `Cmcoordinator.addin` en la raiz y otro dentro de `OpenProject.Revit\`.

Solucion:
- Debe quedar solo el manifiesto raiz:
  `C:\ProgramData\Autodesk\Revit\Addins\2026\Cmcoordinator.addin`

## Checklist Rapido
1. Cerrar Revit.
2. Compilar `Debug-2026`.
3. Borrar `OpenProject.Revit` de `AppData` y `ProgramData`.
4. Instalar una sola copia en `ProgramData`.
5. Confirmar que no queda nada en `AppData`.
6. Confirmar conteo `1` de manifiesto y `1` de `OpenProject.Browser.exe`.
7. Abrir Revit.
8. Revisar `build stamp` en logs.
