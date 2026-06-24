#define AppName      "Replixer"
#define AppVersion   "1.4.5"
#define AppPublisher "RE Studio"
#define AppExeName   "Replixer.exe"
#define SourceDir    "..\publish"
#define IconFile     "..\Assets\Icons\IconApp.ico"

[Setup]
AppId={{A3F2B1C4-7E8D-4F5A-9C3B-2D6E1F0A8B7C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
OutputDir=.
OutputBaseFilename=Replixer-Setup
LicenseFile=LICENSE.txt
SetupIconFile={#IconFile}
Compression=none
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

[Languages]
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "english";   MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Створити ярлик на робочому столі"; GroupDescription: "Додаткові ярлики:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#AppName}";           Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{userprograms}\Видалити {#AppName}";  Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}";            Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Перший запуск після ручного встановлення (показується checkbox у wizard)
Filename: "{app}\{#AppExeName}"; Description: "Запустити {#AppName}"; Flags: nowait postinstall skipifsilent
; Автоматичний перезапуск після тихого оновлення (/SILENT)
Filename: "{app}\{#AppExeName}"; Parameters: "--tray"; Flags: nowait skipifnotsilent

[UninstallRun]
Filename: "{app}\{#AppExeName}"; Parameters: "--uninstall"; Flags: skipifdoesntexist runhidden; RunOnceId: "RemoveAutostart"
