#define RepoRoot ".."
#define AppName "Cmcoordinator Revit Add-in"
#define AppPublisher "Cmcoordinator"
#define AddinName "Cmcoordinator"
#define BuildStamp GetDateTimeString('yyyymmdd-hhnnss', '-', ':')
#define BrowserBinariesSource "..\src\OpenProject.Browser\bin\x64\Release-2026\net48\*"
#define AddinManifestSource "..\src\OpenProject.Revit\Cmcoordinator.addin"
#define AppIdValue "{{D22B2A9A-2C49-4C3A-8F8B-6AC9A2E6A026}"
#define UninstallRegKey "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{{D22B2A9A-2C49-4C3A-8F8B-6AC9A2E6A026}_is1"

; One Release build per Revit year (2019-2027). ReleaseCurrentOnly packages only 2026 and 2027.
#if !Defined(ReleaseCurrentOnly) && FileExists("..\src\OpenProject.Revit\bin\x64\Release-2019\net472\OpenProject.Revit.dll")
  #define HasRevit2019
#endif
#if !Defined(ReleaseCurrentOnly) && FileExists("..\src\OpenProject.Revit\bin\x64\Release-2020\net472\OpenProject.Revit.dll")
  #define HasRevit2020
#endif
#if !Defined(ReleaseCurrentOnly) && FileExists("..\src\OpenProject.Revit\bin\x64\Release-2021\net48\OpenProject.Revit.dll")
  #define HasRevit2021
#endif
#if !Defined(ReleaseCurrentOnly) && FileExists("..\src\OpenProject.Revit\bin\x64\Release-2022\net48\OpenProject.Revit.dll")
  #define HasRevit2022
#endif
#if !Defined(ReleaseCurrentOnly) && FileExists("..\src\OpenProject.Revit\bin\x64\Release-2023\net48\OpenProject.Revit.dll")
  #define HasRevit2023
#endif
#if !Defined(ReleaseCurrentOnly) && FileExists("..\src\OpenProject.Revit\bin\x64\Release-2024\net48\OpenProject.Revit.dll")
  #define HasRevit2024
#endif
#if !Defined(ReleaseCurrentOnly) && FileExists("..\src\OpenProject.Revit\bin\x64\Release-2025\net8.0-windows\OpenProject.Revit.dll")
  #define HasRevit2025
#endif
#if FileExists("..\src\OpenProject.Revit\bin\x64\Release-2026\net8.0-windows\OpenProject.Revit.dll")
  #define HasRevit2026
#endif
#if FileExists("..\src\OpenProject.Revit\bin\x64\Release-2027\net10.0-windows7.0\OpenProject.Revit.dll")
  #define HasRevit2027
#endif
#if FileExists("..\src\OpenProject.Revit\bin\x64\Release-2026\net8.0-windows\OpenProject.Revit.dll")
  #define AppVersion GetVersionNumbersString("..\src\OpenProject.Revit\bin\x64\Release-2026\net8.0-windows\OpenProject.Revit.dll")
#elif FileExists("..\src\OpenProject.Revit\bin\x64\Release-2027\net10.0-windows7.0\OpenProject.Revit.dll")
  #define AppVersion GetVersionNumbersString("..\src\OpenProject.Revit\bin\x64\Release-2027\net10.0-windows7.0\OpenProject.Revit.dll")
#endif
#if !Defined(AppVersion)
  #error No se encontraron binarios de OpenProject.Revit para crear el instalador.
#endif
[Setup]
AppId={#AppIdValue}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={commonappdata}\Autodesk\Revit\Addins
DisableDirPage=yes
DirExistsWarning=no
DisableProgramGroupPage=yes
DisableReadyMemo=no
DisableWelcomePage=no
OutputDir={#RepoRoot}\output\installer
#if Defined(ReleaseTag)
OutputBaseFilename=Cmcoordinator-{#ReleaseTag}-setup
#else
OutputBaseFilename=Cmcoordinator-Revit-Addin-Setup-{#BuildStamp}
#endif
SetupIconFile={#RepoRoot}\Assets\CMicon.ico
WizardImageFile={#RepoRoot}\Assets\Cmcoordinator_LOGO.bmp
Compression=lzma
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={uninstallexe}
CloseApplications=no
UsePreviousAppDir=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Components]
#if Defined(HasRevit2019)
Name: "revit2019"; Description: "Autodesk Revit 2019"; Flags: disablenouninstallwarning
#endif
#if Defined(HasRevit2020)
Name: "revit2020"; Description: "Autodesk Revit 2020"; Flags: disablenouninstallwarning
#endif
#if Defined(HasRevit2021)
Name: "revit2021"; Description: "Autodesk Revit 2021"; Flags: disablenouninstallwarning
#endif
#if Defined(HasRevit2022)
Name: "revit2022"; Description: "Autodesk Revit 2022"; Flags: disablenouninstallwarning
#endif
#if Defined(HasRevit2023)
Name: "revit2023"; Description: "Autodesk Revit 2023"; Flags: disablenouninstallwarning
#endif
#if Defined(HasRevit2024)
Name: "revit2024"; Description: "Autodesk Revit 2024"; Flags: disablenouninstallwarning
#endif
#if Defined(HasRevit2025)
Name: "revit2025"; Description: "Autodesk Revit 2025"; Flags: disablenouninstallwarning
#endif
#if Defined(HasRevit2026)
Name: "revit2026"; Description: "Autodesk Revit 2026"; Flags: disablenouninstallwarning
#endif
#if Defined(HasRevit2027)
Name: "revit2027"; Description: "Autodesk Revit 2027"; Flags: disablenouninstallwarning
#endif

[Dirs]
#if Defined(HasRevit2019)
Name: "{code:GetPluginTargetForYear|2019}"; Components: revit2019
Name: "{code:GetBrowserTargetForYear|2019}"; Components: revit2019
#endif
#if Defined(HasRevit2020)
Name: "{code:GetPluginTargetForYear|2020}"; Components: revit2020
Name: "{code:GetBrowserTargetForYear|2020}"; Components: revit2020
#endif
#if Defined(HasRevit2021)
Name: "{code:GetPluginTargetForYear|2021}"; Components: revit2021
Name: "{code:GetBrowserTargetForYear|2021}"; Components: revit2021
#endif
#if Defined(HasRevit2022)
Name: "{code:GetPluginTargetForYear|2022}"; Components: revit2022
Name: "{code:GetBrowserTargetForYear|2022}"; Components: revit2022
#endif
#if Defined(HasRevit2023)
Name: "{code:GetPluginTargetForYear|2023}"; Components: revit2023
Name: "{code:GetBrowserTargetForYear|2023}"; Components: revit2023
#endif
#if Defined(HasRevit2024)
Name: "{code:GetPluginTargetForYear|2024}"; Components: revit2024
Name: "{code:GetBrowserTargetForYear|2024}"; Components: revit2024
#endif
#if Defined(HasRevit2025)
Name: "{code:GetPluginTargetForYear|2025}"; Components: revit2025
Name: "{code:GetBrowserTargetForYear|2025}"; Components: revit2025
#endif
#if Defined(HasRevit2026)
Name: "{code:GetPluginTargetForYear|2026}"; Components: revit2026
Name: "{code:GetBrowserTargetForYear|2026}"; Components: revit2026
#endif
#if Defined(HasRevit2027)
Name: "{code:GetPluginTargetForYear|2027}"; Components: revit2027
Name: "{code:GetBrowserTargetForYear|2027}"; Components: revit2027
#endif

[Files]
#if Defined(HasRevit2019)
Source: "..\src\OpenProject.Revit\Cmcoordinator.addin"; DestDir: "{code:GetInstallTargetForYear|2019}"; DestName: "{#AddinName}.addin"; Flags: ignoreversion; Components: revit2019
Source: "..\src\OpenProject.Revit\bin\x64\Release-2019\net472\*"; DestDir: "{code:GetPluginTargetForYear|2019}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2019
Source: "{#BrowserBinariesSource}"; DestDir: "{code:GetBrowserTargetForYear|2019}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2019
#endif
#if Defined(HasRevit2020)
Source: "..\src\OpenProject.Revit\Cmcoordinator.addin"; DestDir: "{code:GetInstallTargetForYear|2020}"; DestName: "{#AddinName}.addin"; Flags: ignoreversion; Components: revit2020
Source: "..\src\OpenProject.Revit\bin\x64\Release-2020\net472\*"; DestDir: "{code:GetPluginTargetForYear|2020}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2020
Source: "{#BrowserBinariesSource}"; DestDir: "{code:GetBrowserTargetForYear|2020}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2020
#endif
#if Defined(HasRevit2021)
Source: "..\src\OpenProject.Revit\Cmcoordinator.addin"; DestDir: "{code:GetInstallTargetForYear|2021}"; DestName: "{#AddinName}.addin"; Flags: ignoreversion; Components: revit2021
Source: "..\src\OpenProject.Revit\bin\x64\Release-2021\net48\*"; DestDir: "{code:GetPluginTargetForYear|2021}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2021
Source: "{#BrowserBinariesSource}"; DestDir: "{code:GetBrowserTargetForYear|2021}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2021
#endif
#if Defined(HasRevit2022)
Source: "..\src\OpenProject.Revit\Cmcoordinator.addin"; DestDir: "{code:GetInstallTargetForYear|2022}"; DestName: "{#AddinName}.addin"; Flags: ignoreversion; Components: revit2022
Source: "..\src\OpenProject.Revit\bin\x64\Release-2022\net48\*"; DestDir: "{code:GetPluginTargetForYear|2022}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2022
Source: "{#BrowserBinariesSource}"; DestDir: "{code:GetBrowserTargetForYear|2022}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2022
#endif
#if Defined(HasRevit2023)
Source: "..\src\OpenProject.Revit\Cmcoordinator.addin"; DestDir: "{code:GetInstallTargetForYear|2023}"; DestName: "{#AddinName}.addin"; Flags: ignoreversion; Components: revit2023
Source: "..\src\OpenProject.Revit\bin\x64\Release-2023\net48\*"; DestDir: "{code:GetPluginTargetForYear|2023}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2023
Source: "{#BrowserBinariesSource}"; DestDir: "{code:GetBrowserTargetForYear|2023}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2023
#endif
#if Defined(HasRevit2024)
Source: "..\src\OpenProject.Revit\Cmcoordinator.addin"; DestDir: "{code:GetInstallTargetForYear|2024}"; DestName: "{#AddinName}.addin"; Flags: ignoreversion; Components: revit2024
Source: "..\src\OpenProject.Revit\bin\x64\Release-2024\net48\*"; DestDir: "{code:GetPluginTargetForYear|2024}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2024
Source: "{#BrowserBinariesSource}"; DestDir: "{code:GetBrowserTargetForYear|2024}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2024
#endif
#if Defined(HasRevit2025)
Source: "..\src\OpenProject.Revit\Cmcoordinator.addin"; DestDir: "{code:GetInstallTargetForYear|2025}"; DestName: "{#AddinName}.addin"; Flags: ignoreversion; Components: revit2025
Source: "..\src\OpenProject.Revit\bin\x64\Release-2025\net8.0-windows\*"; DestDir: "{code:GetPluginTargetForYear|2025}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2025
Source: "{#BrowserBinariesSource}"; DestDir: "{code:GetBrowserTargetForYear|2025}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2025
#endif
#if Defined(HasRevit2026)
Source: "..\src\OpenProject.Revit\Cmcoordinator.addin"; DestDir: "{code:GetInstallTargetForYear|2026}"; DestName: "{#AddinName}.addin"; Flags: ignoreversion; Components: revit2026
Source: "..\src\OpenProject.Revit\bin\x64\Release-2026\net8.0-windows\*"; DestDir: "{code:GetPluginTargetForYear|2026}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2026
Source: "{#BrowserBinariesSource}"; DestDir: "{code:GetBrowserTargetForYear|2026}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2026
#endif
#if Defined(HasRevit2027)
Source: "..\src\OpenProject.Revit\Cmcoordinator.addin"; DestDir: "{code:GetInstallTargetForYear|2027}"; DestName: "{#AddinName}.addin"; Flags: ignoreversion; Components: revit2027
Source: "..\src\OpenProject.Revit\bin\x64\Release-2027\net10.0-windows7.0\*"; DestDir: "{code:GetPluginTargetForYear|2027}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2027
Source: "{#BrowserBinariesSource}"; DestDir: "{code:GetBrowserTargetForYear|2027}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2027
#endif

[InstallDelete]
#if Defined(HasRevit2019)
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2019\{#AddinName}.addin"; Components: revit2019
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2019\{#AddinName}"; Components: revit2019
#endif
#if Defined(HasRevit2020)
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2020\{#AddinName}.addin"; Components: revit2020
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2020\{#AddinName}"; Components: revit2020
#endif
#if Defined(HasRevit2021)
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2021\{#AddinName}.addin"; Components: revit2021
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2021\{#AddinName}"; Components: revit2021
#endif
#if Defined(HasRevit2022)
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2022\{#AddinName}.addin"; Components: revit2022
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2022\{#AddinName}"; Components: revit2022
#endif
#if Defined(HasRevit2023)
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2023\{#AddinName}.addin"; Components: revit2023
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2023\{#AddinName}"; Components: revit2023
#endif
#if Defined(HasRevit2024)
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2024\{#AddinName}.addin"; Components: revit2024
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2024\{#AddinName}"; Components: revit2024
#endif
#if Defined(HasRevit2025)
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2025\{#AddinName}.addin"; Components: revit2025
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2025\{#AddinName}"; Components: revit2025
#endif
#if Defined(HasRevit2026)
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2026\{#AddinName}.addin"; Components: revit2026
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2026\{#AddinName}"; Components: revit2026
#endif
#if Defined(HasRevit2027)
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2027\{#AddinName}.addin"; Components: revit2027
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2027\{#AddinName}"; Components: revit2027
#endif

[UninstallDelete]
#if Defined(HasRevit2019)
Type: files; Name: "{app}\2019\{#AddinName}.addin"; Components: revit2019
Type: filesandordirs; Name: "{app}\2019\{#AddinName}"; Components: revit2019
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2019\{#AddinName}.addin"; Components: revit2019
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2019\{#AddinName}"; Components: revit2019
#endif
#if Defined(HasRevit2020)
Type: files; Name: "{app}\2020\{#AddinName}.addin"; Components: revit2020
Type: filesandordirs; Name: "{app}\2020\{#AddinName}"; Components: revit2020
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2020\{#AddinName}.addin"; Components: revit2020
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2020\{#AddinName}"; Components: revit2020
#endif
#if Defined(HasRevit2021)
Type: files; Name: "{app}\2021\{#AddinName}.addin"; Components: revit2021
Type: filesandordirs; Name: "{app}\2021\{#AddinName}"; Components: revit2021
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2021\{#AddinName}.addin"; Components: revit2021
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2021\{#AddinName}"; Components: revit2021
#endif
#if Defined(HasRevit2022)
Type: files; Name: "{app}\2022\{#AddinName}.addin"; Components: revit2022
Type: filesandordirs; Name: "{app}\2022\{#AddinName}"; Components: revit2022
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2022\{#AddinName}.addin"; Components: revit2022
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2022\{#AddinName}"; Components: revit2022
#endif
#if Defined(HasRevit2023)
Type: files; Name: "{app}\2023\{#AddinName}.addin"; Components: revit2023
Type: filesandordirs; Name: "{app}\2023\{#AddinName}"; Components: revit2023
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2023\{#AddinName}.addin"; Components: revit2023
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2023\{#AddinName}"; Components: revit2023
#endif
#if Defined(HasRevit2024)
Type: files; Name: "{app}\2024\{#AddinName}.addin"; Components: revit2024
Type: filesandordirs; Name: "{app}\2024\{#AddinName}"; Components: revit2024
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2024\{#AddinName}.addin"; Components: revit2024
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2024\{#AddinName}"; Components: revit2024
#endif
#if Defined(HasRevit2025)
Type: files; Name: "{app}\2025\{#AddinName}.addin"; Components: revit2025
Type: filesandordirs; Name: "{app}\2025\{#AddinName}"; Components: revit2025
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2025\{#AddinName}.addin"; Components: revit2025
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2025\{#AddinName}"; Components: revit2025
#endif
#if Defined(HasRevit2026)
Type: files; Name: "{app}\2026\{#AddinName}.addin"; Components: revit2026
Type: filesandordirs; Name: "{app}\2026\{#AddinName}"; Components: revit2026
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2026\{#AddinName}.addin"; Components: revit2026
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2026\{#AddinName}"; Components: revit2026
#endif
#if Defined(HasRevit2027)
Type: files; Name: "{commonpf64}\Autodesk\Revit\Addins\2027\{#AddinName}.addin"; Components: revit2027
Type: filesandordirs; Name: "{commonpf64}\Autodesk\Revit\Addins\2027\{#AddinName}"; Components: revit2027
Type: files; Name: "{userappdata}\Autodesk\REVIT\Addins\2027\{#AddinName}.addin"; Components: revit2027
Type: filesandordirs; Name: "{userappdata}\Autodesk\REVIT\Addins\2027\{#AddinName}"; Components: revit2027
#endif

[Code]
#include "Code/DetectionAndOperations.iss"
#include "Code/WizardLifecycle.iss"
