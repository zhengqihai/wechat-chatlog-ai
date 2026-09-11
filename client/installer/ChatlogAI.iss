; ============================================================================
; Chatlog AI —— Inno Setup 安装脚本
;
; 正常用法：由 client\build-installer.ps1 调用（脚本会先组装好 StageDir 再编译本文件）。
; 也可以直接用 Inno Setup 编译器打开本文件编译，但前提是 client\build\stage 已存在。
;
; 编码：必须保存为 UTF-8 with BOM。Inno Setup 6.0+ 都支持带 BOM 的 UTF-8；
;       若去掉 BOM，则 6.0~6.2 会把中文读成乱码（6.3 起才允许无 BOM）。
;
; 三条不可随意改动的约定（改动前请先读完）：
;   1. AppId 永久固定。它是"这是同一个程序"的唯一身份，换掉它会导致新包无法覆盖
;      升级旧安装，用户机器上会出现两份互不认识的 Chatlog AI。
;   2. 只打包客户端本体（ChatlogAI.exe + app\ + runtime\）。
;      config\ / data\ / logs\ / api-keys.json 一律不进包——它们由程序首次运行时在
;      %ProgramData%\ChatlogAI 下自行生成（见 Bootstrap\ConfigBootstrapper.cs）。
;   3. DisableDirPage 必须显式写 no。它的默认值是 auto，一旦检测到同 AppId 的旧安装，
;      安装向导会隐藏「选择目标位置」页并沿用旧目录，用户就无法自选安装位置了。
; ============================================================================

; ---------- 可被命令行 /D 覆盖的编译期变量（给出手工编译时的默认值） ----------

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

; VersionInfoVersion 只接受 X.X.X.X 这样的纯数字，所以单独传一个。
#ifndef AppVersionNumeric
  #define AppVersionNumeric "1.0.0.0"
#endif

; SourcePath 是 ISPP 预定义变量：当前脚本所在目录（client\installer）。
#ifndef StageDir
  #define StageDir AddBackslash(SourcePath) + "..\build\stage"
#endif

#ifndef OutputDir
  #define OutputDir AddBackslash(SourcePath) + "..\build\dist"
#endif

#ifndef AppIcon
  #define AppIcon AddBackslash(SourcePath) + "..\ChatlogAI.Client\Assets\app.ico"
#endif

[Setup]
AppId={{998A14ED-7728-4818-8E40-A121C7C54333}
AppName=Chatlog AI
AppVersion={#AppVersion}
AppVerName=Chatlog AI {#AppVersion}
AppPublisher=Chatlog AI
DefaultDirName={autopf}\Chatlog AI
DefaultGroupName=Chatlog AI
; 允许用户在向导中自选安装位置（默认值 auto 会在升级时隐藏该页）。
DisableDirPage=no
DisableProgramGroupPage=yes
AllowNoIcons=yes
; 与 app.manifest 的 requireAdministrator 保持一致：程序本体需要管理员权限。
PrivilegesRequired=admin
; auto 目标：{autopf} 解析为 64 位 Program Files。
; x64compatible 需要 Inno Setup 6.3 及以上（6.3 起 x64 被弃用）。
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; .NET 8 桌面运行时支持的最低 Windows 10 版本（1607 / Redstone 1）。
MinVersion=10.0.14393
OutputDir={#OutputDir}
OutputBaseFilename=ChatlogAI-Setup-{#AppVersion}
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\ChatlogAI.exe
UninstallDisplayName=Chatlog AI {#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 覆盖安装时先自动关掉正在运行的旧版本，但不要自动把它拉起来——
; 拉起由下面 [Run] 段的 postinstall 勾选项负责，避免出现两个入口。
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductName=Chatlog AI
VersionInfoProductVersion={#AppVersionNumeric}
VersionInfoDescription=Chatlog AI Setup
VersionInfoCompany=Chatlog AI

[Languages]
; 只使用 Inno Setup 自带的英文语言文件，不引入任何第三方 .isl。
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; StageDir 已经由 build-installer.ps1 组装好，这里整棵树原样搬进 {app}。
; ignoreversion：程序版本号长期为 1.0.0，若用默认的版本比较策略，覆盖升级时
; 同名同版本的 ChatlogAI.exe 会被"跳过"，等于没更新，因此必须强制覆盖。
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Chatlog AI"; Filename: "{app}\ChatlogAI.exe"; WorkingDir: "{app}"; Comment: "启动 Chatlog AI"
; 对应被退役的「首次初始化.bat」：强制重新进入微信授权阶段，用于更换账号或刷新密钥。
Name: "{autoprograms}\Chatlog AI 重新初始化"; Filename: "{app}\ChatlogAI.exe"; Parameters: "--initialize"; WorkingDir: "{app}"; Comment: "更换微信账号或刷新账号密钥"
Name: "{autodesktop}\Chatlog AI"; Filename: "{app}\ChatlogAI.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ChatlogAI.exe"; Description: "{cm:LaunchProgram,Chatlog AI}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DataFolderName = 'ChatlogAI';

{ 卸载时询问是否连带删除 %ProgramData%\ChatlogAI。
  默认按钮是「否」，即默认保留用户数据；静默卸载不弹窗、直接保留。 }
function InitializeUninstall(): Boolean;
var
  DataDir: String;
begin
  Result := True;

  if UninstallSilent then
    Exit;

  DataDir := ExpandConstant('{commonappdata}\') + DataFolderName;
  if not DirExists(DataDir) then
    Exit;

  if MsgBox(
       '是否同时删除 Chatlog AI 的本机数据？' + #13#10 + #13#10 +
       '包含：模型 API Key、自定义提示词、微信聊天缓存与导出记录。' + #13#10 +
       '位置：' + DataDir + #13#10 + #13#10 +
       '选择「否」将保留以上数据，日后重新安装可继续使用。' + #13#10 +
       '（若程序仍在运行，请先退出后再卸载。）',
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYes then
  begin
    DelTree(DataDir, True, True, True);
  end;
end;
