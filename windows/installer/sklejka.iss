; Per-user installer, no admin rights. Registry writes are done by Inno itself (no reg.exe / cmd).
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6C3E0F7B-5A2D-4C59-9D0B-1E2A7C5B9F41}
AppName=Склейка файлов
AppVersion={#AppVersion}
AppPublisher=Ivan Vorontsov
DefaultDirName={localappdata}\Programs\Sklejka
DefaultGroupName=Склейка файлов
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DisableProgramGroupPage=yes
OutputBaseFilename=Sklejka-Setup-{#AppVersion}
OutputDir=..\dist
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
SetupIconFile=..\Sklejka.App\app.ico
UninstallDisplayIcon={app}\Sklejka.exe
ChangesAssociations=yes
CloseApplications=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; Flags: unchecked
Name: "shellmenu"; Description: "Пункт «Склеить» в меню проводника для .001, .apks, .xapk, .apkm"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Склейка файлов"; Filename: "{app}\Sklejka.exe"
Name: "{autodesktop}\Склейка файлов"; Filename: "{app}\Sklejka.exe"; Tasks: desktopicon

[Registry]
; ProgID + «Открыть с помощью»
Root: HKCU; Subkey: "Software\Classes\Sklejka.File"; ValueType: string; ValueData: "Части для склейки"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Sklejka.File\DefaultIcon"; ValueType: string; ValueData: """{app}\Sklejka.exe"",0"
Root: HKCU; Subkey: "Software\Classes\Sklejka.File\shell\open\command"; ValueType: string; ValueData: """{app}\Sklejka.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.001\OpenWithProgids"; ValueType: none; ValueName: "Sklejka.File"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.apks\OpenWithProgids"; ValueType: none; ValueName: "Sklejka.File"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.xapk\OpenWithProgids"; ValueType: none; ValueName: "Sklejka.File"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.apkm\OpenWithProgids"; ValueType: none; ValueName: "Sklejka.File"; Flags: uninsdeletevalue
; context menu «Склеить»
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.001\shell\Sklejka"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Склеить"; Flags: uninsdeletekey; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.001\shell\Sklejka"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Sklejka.exe"",0"; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.001\shell\Sklejka\command"; ValueType: string; ValueData: """{app}\Sklejka.exe"" ""%1"""; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.apks\shell\Sklejka"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Склеить в один APK"; Flags: uninsdeletekey; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.apks\shell\Sklejka"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Sklejka.exe"",0"; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.apks\shell\Sklejka\command"; ValueType: string; ValueData: """{app}\Sklejka.exe"" ""%1"""; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.xapk\shell\Sklejka"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Склеить в один APK"; Flags: uninsdeletekey; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.xapk\shell\Sklejka"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Sklejka.exe"",0"; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.xapk\shell\Sklejka\command"; ValueType: string; ValueData: """{app}\Sklejka.exe"" ""%1"""; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.apkm\shell\Sklejka"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Склеить в один APK"; Flags: uninsdeletekey; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.apkm\shell\Sklejka"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Sklejka.exe"",0"; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.apkm\shell\Sklejka\command"; ValueType: string; ValueData: """{app}\Sklejka.exe"" ""%1"""; Tasks: shellmenu

[Run]
Filename: "{app}\Sklejka.exe"; Description: "Запустить «Склейку файлов»"; Flags: nowait postinstall skipifsilent
