; AnMusic 安装包脚本（Inno Setup 6）
; 按用户安装：无需管理员权限，安装到 %LocalAppData%\Programs\AnMusic

#define MyAppName "AnMusic"
#define MyAppVersion "3.0.1"
#define MyAppExeName "AnMusic.exe"
#define MyAppPublisher "AnMusic"

[Setup]
AppId={{8E4F2A6B-9C1D-4E7A-B3F5-2D8C0A1E5F9B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 主输出为单个自包含 exe
OutputDir=installer
OutputBaseFilename=AnMusic-Setup-{#MyAppVersion}
SetupIconFile=src\AnMusic\Assets\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 按用户安装，无需管理员
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
; 中文向导
ShowLanguageDialog=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "installer\ChineseSimplified.isl"

[Files]
Source: "publish\AnMusic.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "src\AnMusic\Assets\app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: checkedonce

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时保留用户数据（%LocalAppData%\AnMusic），仅删除程序文件
Type: filesandordirs; Name: "{app}"
