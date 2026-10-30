#Requires -Version 5.1
<#
.SYNOPSIS
    把 Chatlog AI 客户端打包成可直接分发的安装包（client\build\dist\ChatlogAI-Setup-x.y.z.exe）。

    ⚠️ 本文件**必须**保存为 UTF-8 **with BOM**。PowerShell 5.1 在无 BOM 时按系统代码页（中文版是 GBK）
       解析脚本，中文注释会变成乱码并连带把引号、括号吃掉，报出一堆"Unexpected token"。
       任何编辑器/工具改完本文件后，请确认开头三字节仍是 EF BB BF（与 installer\ChatlogAI.iss 同一要求）。

.DESCRIPTION
    每次代码更新后重跑本脚本即可：产物会覆盖上一次的结果，收到旧包的人直接装新包就能升级
    （覆盖安装不会动他的 ProgramData 配置，也不会要求先卸载）。

    脚本按顺序做四件事：
      1. dotnet publish（Release / win-x64 / self-contained）
         把 .NET 运行时一起打进去，对方电脑无需预装 .NET。
      2. 组装 staging：把仓库的 app\ 与 runtime\ 拷进去，剔除 Python 字节码缓存。
      3. 安全自检（硬闸门）：确认 staging 内没有任何密钥、聊天数据、日志、导出文件。
         一旦命中就立刻中止，绝不产出安装包。
      4. 调用 Inno Setup 编译器，把 staging 打成安装包。

    用法：
        powershell -ExecutionPolicy Bypass -File .\client\build-installer.ps1

    首次使用前需要先安装 Inno Setup 6.3 或更高版本：https://jrsoftware.org/isdl.php

.PARAMETER Version
    覆盖版本号，例如 -Version 1.2.0。默认读取 ChatlogAI.Client.csproj 的 <Version>。

.PARAMETER SkipPublish
    跳过 dotnet publish，复用上次 staging 里的客户端产物。仅用于反复调试打包流程。

.PARAMETER SkipInstaller
    只组装 staging 并做安全自检，不编译安装包。此模式下不需要安装 Inno Setup。
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Bump,
    [ValidateSet('major', 'minor', 'patch')]
    [string]$BumpPart = 'patch',
    [switch]$SkipPublish,
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# dotnet 与 ISCC 的输出都是 UTF-8，而中文版 Windows 的控制台默认按 GBK 解码，
# 不修正的话它们的提示会显示成乱码。仅影响显示，失败时忽略即可。
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
}
catch {
}

# ============================================================================
# 路径常量：全部由脚本自身位置推导，因此在任何工作目录下执行结果都一致。
# ============================================================================
$ClientDir    = $PSScriptRoot
$RepoRoot     = Split-Path -Parent $ClientDir
$ProjectFile  = Join-Path $ClientDir 'ChatlogAI.Client\ChatlogAI.Client.csproj'
$IssFile      = Join-Path $ClientDir 'installer\ChatlogAI.iss'
$BuildDir     = Join-Path $ClientDir 'build'
$StageDir     = Join-Path $BuildDir 'stage'
$DistDir      = Join-Path $BuildDir 'dist'

$VersionFile  = Join-Path $ClientDir 'VERSION'

$AppSource     = Join-Path $RepoRoot 'app'
$RuntimeSource = Join-Path $RepoRoot 'runtime'

$InstallerDownloadUrl = 'https://jrsoftware.org/isdl.php'

# ============================================================================
# 输出helpers
# ============================================================================
function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Write-Detail([string]$Text) {
    Write-Host "    $Text"
}

function Write-Note([string]$Text) {
    Write-Host "    $Text" -ForegroundColor Yellow
}

function Stop-WithError([string]$Text) {
    Write-Host ''
    Write-Host "错误：$Text" -ForegroundColor Red
    Write-Host ''
    exit 1
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N1} KB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

# ============================================================================
# NuGet 家目录：某些受控环境（CI / 沙箱）不允许写 %APPDATA%\NuGet，
# dotnet restore 会直接失败。这里先探测，只有不可写时才把 NuGet 的家目录与全局包
# 缓存搬进仓库内的隐藏目录，普通开发机上不会有任何行为变化。
# ============================================================================
function Test-WritableDirectory([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    try {
        New-Item -ItemType Directory -Force -Path $Path -ErrorAction Stop | Out-Null
        $probe = Join-Path $Path ('.write-probe-' + [Guid]::NewGuid().ToString('N'))
        [IO.File]::WriteAllBytes($probe, [byte[]]@())
        Remove-Item -LiteralPath $probe -Force -ErrorAction Stop
        return $true
    }
    catch {
        return $false
    }
}

function Initialize-NuGetHome {
    $appData = $env:APPDATA
    if (-not [string]::IsNullOrWhiteSpace($appData) -and
        (Test-WritableDirectory (Join-Path $appData 'NuGet'))) {
        return
    }

    Write-Note "默认 NuGet 缓存目录（$appData\NuGet）不可用，本次构建改用仓库内隔离目录。"
    $env:APPDATA = Join-Path $RepoRoot '.dotnet-appdata'
    $env:NUGET_PACKAGES = Join-Path $RepoRoot '.nuget-packages'
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:NUGET_PACKAGES | Out-Null
}

# ============================================================================
# 复制助手：robocopy 处理几十万个小文件比 Copy-Item 快一个数量级，
# 且原生支持排除目录名与文件通配符。
# robo 退出码 0~7 均表示成功（1=有文件被复制），>=8 才是真失败。
# ============================================================================
function Copy-Tree([string]$Source, [string]$Destination) {
    $roboArgs = @(
        $Source, $Destination,
        '/E',                       # 含空目录
        '/COPY:DAT',                # 数据 + 属性 + 时间戳
        '/R:1', '/W:1',             # 失败重试 1 次，避免长时间挂住
        '/MT:8',                    # 多线程复制
        '/XD', '__pycache__',       # 任意层级排除 Python 字节码缓存
        '/XF', '*.pyc', '*.pyo',
        '/NFL', '/NDL', '/NJH', '/NJS', '/NP'
    )
    & robocopy @roboArgs | Out-Null
    if ($LASTEXITCODE -ge 8) {
        Stop-WithError "复制失败（robocopy 退出码 $LASTEXITCODE）：$Source -> $Destination"
    }
}

# 尽力删除整棵目录树。
# 之所以不写成一行 Remove-Item，是因为编辑器 / 杀软 / 上一个残留进程会给个别文件加锁
# （典型是 Electron 应用的 .asar 归档：Trae 会长期持有它）。这种情况下删除失败，
# 但随后的 robocopy 复制是"覆盖写入"，仍能把该文件刷新成最新版本，所以不该让整个打包中止。
# 删除顺序：先整树试一次 -> 再逐个文件删 -> 再自底向上删空目录。
function Remove-Tree([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }

    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $Path)) { return }

    foreach ($file in @(Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $file.FullName -Force -Confirm:$false -ErrorAction SilentlyContinue
    }
    $dirs = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -Directory -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } -Descending)
    foreach ($dir in $dirs) {
        Remove-Item -LiteralPath $dir.FullName -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $Path -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue

    if (Test-Path -LiteralPath $Path) {
        $remaining = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue)
        Write-Note "未能完全清空（$($remaining.Count) 项被其它进程占用）：$Path"
        foreach ($item in $remaining) { Write-Detail "残留 $($item.FullName)" }
        Write-Note '残留项会在下一步复制时被覆盖刷新；若上述残留项已从源码中删除，请关闭占用它的程序后重跑本脚本。'
    }
}

# ============================================================================
# dotnet 定位：SDK 装在默认位置时常常没有被加进 PATH，因此不能只依赖 Get-Command。
# ============================================================================
function Find-Dotnet {
    $onPath = Get-Command 'dotnet' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    foreach ($root in @($env:ProgramFiles, "${env:ProgramFiles(x86)}")) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $candidates += (Join-Path $root 'dotnet\dotnet.exe')
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }

    return $null
}

# ============================================================================
# ISCC.exe 定位与版本校验
# ============================================================================
function Find-Iscc {
    $candidates = New-Object System.Collections.Generic.List[string]

    if ($env:INNO_SETUP_ISCC) { $candidates.Add($env:INNO_SETUP_ISCC) }

    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { $candidates.Add($onPath.Source) }

    foreach ($root in @("${env:ProgramFiles(x86)}", $env:ProgramFiles)) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        foreach ($major in @('7', '6', '5')) {
            $candidates.Add((Join-Path $root "Inno Setup $major\ISCC.exe"))
        }
    }

    # 兜底：从卸载注册表里找安装位置（自定义安装目录的情况）。
    foreach ($hive in @(
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        Get-ChildItem -Path $hive -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -like 'Inno Setup*' } |
            ForEach-Object {
                $location = (Get-ItemProperty -Path $_.PSPath -ErrorAction SilentlyContinue).InstallLocation
                if (-not [string]::IsNullOrWhiteSpace($location)) {
                    $candidates.Add((Join-Path $location 'ISCC.exe'))
                }
            }
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $candidate
        }
    }

    return $null
}

# ISCC.exe 没有内嵌版本资源（FileVersion 恒为 0.0.0.0），所以不能只看文件版本。
# 回退方案：在卸载注册表里找安装目录与 ISCC.exe 同级的 "Inno Setup *" 条目，读它的 DisplayVersion。
function Get-IsccVersion([string]$IsccPath) {
    $rawVersion = (Get-Item -LiteralPath $IsccPath).VersionInfo.FileVersion
    $match = [regex]::Match([string]$rawVersion, '^\d+(\.\d+){1,3}')
    if ($match.Success -and $match.Value -ne '0.0.0.0') {
        try { return [Version]$match.Value }
        catch { }
    }

    $isccDir = (Split-Path -Parent $IsccPath).TrimEnd('\')
    foreach ($hive in @(
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        foreach ($key in @(Get-ChildItem -Path $hive -ErrorAction SilentlyContinue |
                Where-Object { $_.PSChildName -like 'Inno Setup*' })) {
            $props = Get-ItemProperty -Path $key.PSPath -ErrorAction SilentlyContinue
            if ([string]::IsNullOrWhiteSpace($props.InstallLocation)) { continue }
            if ($props.InstallLocation.TrimEnd('\') -ine $isccDir) { continue }
            $versionMatch = [regex]::Match([string]$props.DisplayVersion, '^\d+(\.\d+){1,3}')
            if ($versionMatch.Success) {
                try { return [Version]$versionMatch.Value }
                catch { }
            }
        }
    }

    return $null
}

# ============================================================================
# 版本号
# ============================================================================
# 从纯文本文件读取版本号：只取第一行非空内容，容忍 BOM 与空白。
function Read-VersionFromFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try {
        $raw = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
    } catch {
        return $null
    }
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    $raw = $raw.TrimStart([char]0xFEFF)
    $line = @($raw -split "`r?`n" | ForEach-Object { $_.Trim() } |
            Where-Object { $_ } | Select-Object -First 1)
    if (-not $line) { return $null }
    return $line
}

function Get-BaseVersion {
    # 1. client\VERSION（最常用的手改入口）
    $fromFile = Read-VersionFromFile $VersionFile
    if ($fromFile) { return $fromFile }

    # 2. csproj 的 <Version>（回退，兼容老流程）
    $xml = [xml](Get-Content -LiteralPath $ProjectFile -Raw)
    $declared = @($xml.Project.PropertyGroup | ForEach-Object { $_.Version } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) | Select-Object -First 1
    if ($declared) { return $declared.Trim() }

    return '2.0.0'
}

function Get-AppVersion {
    # 命令行 -Version 依然最高优先级，便于临时试打一个别的版本号。
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        return $Version.Trim()
    }
    return Get-BaseVersion
}

# 自动递增：把 2.0.1 → 2.0.2 / 2.1.0 / 3.0.0。只处理前 3 段数字，
# 保留不了的预发布后缀（如 -beta）会被丢弃，因为安装包 VersionInfo 本来就是纯数字。
function Get-NextVersion([string]$Current, [string]$Part) {
    $segments = @($Current -split '[.\-+]' | Where-Object { $_ -match '^\d+$' } | Select-Object -First 3)
    $nums = @()
    foreach ($s in $segments) { $nums += [int]$s }
    while ($nums.Count -lt 3) { $nums += 0 }

    switch ($Part) {
        'major' { $nums[0]++; $nums[1] = 0; $nums[2] = 0 }
        'minor' { $nums[1]++; $nums[2] = 0 }
        default { $nums[2]++ }
    }
    return ($nums -join '.')
}

# 安装包的 VersionInfoVersion 只接受 X.X.X.X 纯数字形式。
function ConvertTo-NumericVersion([string]$Value) {
    $parts = @($Value -split '[.\-+]' | Where-Object { $_ -match '^\d+$' } | Select-Object -First 4)
    if ($parts.Count -eq 0) { return '0.0.0.0' }
    while ($parts.Count -lt 4) { $parts += '0' }
    return ($parts -join '.')
}

# ============================================================================
# 主流程
# ============================================================================

Write-Host ''
Write-Host 'Chatlog AI —— 客户端打包' -ForegroundColor White
Write-Host "仓库根目录：$RepoRoot"

# ---------- 前置检查 ----------
Write-Step '检查构建环境'

$dotnet = Find-Dotnet
if (-not $dotnet) {
    Stop-WithError "找不到 dotnet。请先安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0"
}
Write-Detail "dotnet     : $dotnet"

foreach ($required in @($ProjectFile, $AppSource, $RuntimeSource, $IssFile)) {
    if (-not (Test-Path -LiteralPath $required)) {
        Stop-WithError "缺少必需的文件或目录：$required"
    }
}
Write-Detail "csproj     : $ProjectFile"
Write-Detail "app\       : $AppSource"
Write-Detail "runtime\   : $RuntimeSource"
Write-Detail "iss        : $IssFile"

$appVersion = Get-AppVersion
$numericVersion = ConvertTo-NumericVersion $appVersion
Write-Detail "版本号     : $appVersion（数字形式 $numericVersion）"

Initialize-NuGetHome

# ---------- 组装 staging ----------
if ($SkipPublish) {
    Write-Step '复用 staging 中已有的客户端产物（-SkipPublish）'
    if (-not (Test-Path -LiteralPath (Join-Path $StageDir 'ChatlogAI.exe'))) {
        Stop-WithError "staging 中没有 ChatlogAI.exe，无法跳过发布。请去掉 -SkipPublish 重跑。"
    }
    Write-Detail $StageDir
}
else {
    # 每次全量重建：源码里删掉的文件不能在包里留下残影，否则会出现
    # "代码已更新但旧模块还在"这类极难排查的问题。
    Write-Step '清理旧的 staging'
    Remove-Tree $StageDir
    New-Item -ItemType Directory -Force -Path $StageDir | Out-Null
    Write-Detail $StageDir

    Write-Step '发布客户端（Release / win-x64 / self-contained）'
    Write-Note 'self-contained 会连同 .NET 运行时一起打包，首次执行需要联网下载运行时包。'

    & $dotnet publish $ProjectFile `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -o $StageDir

    if ($LASTEXITCODE -ne 0) {
        Stop-WithError @"
dotnet publish 失败（退出码 $LASTEXITCODE）。
  最常见原因：无法访问 nuget.org 下载 runtime packs，或客户端代码当前编译不过。
  前者请检查网络后重跑；后者请先执行 dotnet build 修掉编译错误。
"@
    }
}

Write-Step '复制 app\ 与 runtime\ 到 staging'
# 先清掉上一轮的两棵子树：robocopy 只做增量叠加，不会删除目标端已多余的旧文件。
Remove-Tree (Join-Path $StageDir 'app')
Remove-Tree (Join-Path $StageDir 'runtime')
Copy-Tree $AppSource     (Join-Path $StageDir 'app')
Copy-Tree $RuntimeSource (Join-Path $StageDir 'runtime')
Write-Detail 'app\ 与 runtime\ 已就位（已排除 __pycache__ / *.pyc / *.pyo）'

# ---------- 安全自检（硬闸门）----------
Write-Step '安全自检：确认包内不含敏感文件'

$forbiddenExactNames = @(
    'api-keys.json',        # 模型 API Key
    'account_keys.json',    # 微信账号密钥
    'config.json',          # 用户配置（含微信账号）
    'config.json.bak',
    '.env'
)
$forbiddenDirNames = @('__pycache__', 'out-data', '.git')
$forbiddenRootDirs = @('config', 'data', 'logs')

$violations = New-Object System.Collections.Generic.List[string]

foreach ($name in $forbiddenRootDirs) {
    $candidate = Join-Path $StageDir $name
    if (Test-Path -LiteralPath $candidate) {
        $violations.Add("禁止出现在包根的目录：$candidate")
    }
}

Get-ChildItem -LiteralPath $StageDir -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object {
    if ($forbiddenExactNames -contains $_.Name) {
        $violations.Add("敏感文件：$($_.FullName)")
    }
}

Get-ChildItem -LiteralPath $StageDir -Recurse -Force -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    if ($forbiddenDirNames -contains $_.Name) {
        $violations.Add("禁止打包的目录：$($_.FullName)")
    }
}

if ($violations.Count -gt 0) {
    Write-Host ''
    foreach ($violation in $violations) { Write-Host "    $violation" -ForegroundColor Red }
    Stop-WithError "包内检出 $($violations.Count) 处敏感内容，已中止打包（未生成安装包）。"
}
Write-Detail '通过：未检出 api-key、聊天数据、日志、导出文件或字节码缓存。'

# ---------- 运行文件完整性 ----------
Write-Step '校验运行文件完整性'
# 与 Bootstrap\ConfigBootstrapper.RequirePaths() 的必需项**逐项对齐**（两边改动必须同步）。
# config.json 不在其列：它由程序首次运行时在 %ProgramData%\ChatlogAI 下生成。
#
# 上游已恢复（runtime\wechat），授权页（10392）依赖 wechat-backend.exe 与 resources\ui，
# 因此重新列入必需项：缺失会让"首次进入拿不到 db_key"却无提示，宁可打包时立即失败。
$requiredPaths = @(
    (Join-Path $StageDir 'ChatlogAI.exe'),
    (Join-Path $StageDir 'app\server.py'),
    (Join-Path $StageDir 'app\localdb\service.py'),
    (Join-Path $StageDir 'runtime\python\python.exe'),
    (Join-Path $StageDir 'runtime\python-packages'),
    (Join-Path $StageDir 'runtime\python-packages\zstandard'),
    (Join-Path $StageDir 'runtime\wechat\resources\backend\wechat-backend.exe'),
    (Join-Path $StageDir 'runtime\wechat\resources\ui')
)
$missing = @($requiredPaths | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missing.Count -gt 0) {
    foreach ($path in $missing) { Write-Host "    缺少：$path" -ForegroundColor Red }
    Stop-WithError 'staging 缺少必需运行文件，装到用户机器上会启动失败，已中止。'
}
Write-Detail "通过：$($requiredPaths.Count) 项必需文件全部就位。"

$stageSize = (Get-ChildItem -LiteralPath $StageDir -Recurse -Force -File |
    Measure-Object -Property Length -Sum).Sum
Write-Detail "staging 体积：$(Format-Size ([long]$stageSize))"

# ---------- 编译安装包 ----------
if ($SkipInstaller) {
    Write-Step '已指定 -SkipInstaller，跳过安装包编译'
    Write-Detail "staging 已就绪：$StageDir"
    exit 0
}

Write-Step '编译安装包（Inno Setup）'

$iscc = Find-Iscc
if (-not $iscc) {
    Stop-WithError @"
找不到 Inno Setup 编译器 ISCC.exe。
  请先安装 Inno Setup 6.3 或更高版本：$InstallerDownloadUrl
  默认安装到 Program Files 即可被自动识别；装在别处可用环境变量指定：
      `$env:INNO_SETUP_ISCC = 'D:\Inno Setup 6\ISCC.exe'
  只想先验证打包流程（不生成安装包）可以加 -SkipInstaller。
"@
}
Write-Detail "ISCC       : $iscc"

# .iss 里用了 ArchitecturesAllowed=x64compatible，它是 6.3 引入的；
# 更早的版本会报 "Unknown architecture"，这里提前给出可读的提示。
$isccVersion = Get-IsccVersion $iscc
if ($isccVersion) {
    Write-Detail "ISCC 版本  : $isccVersion"
    if ($isccVersion -lt [Version]'6.3') {
        Stop-WithError @"
Inno Setup 版本过低（检测到 $isccVersion，需要 6.3 或更高）。
  本项目 .iss 中使用了 6.3 才支持的 ArchitecturesAllowed=x64compatible。
  请升级：$InstallerDownloadUrl
"@
    }
}
else {
    Write-Note '无法确定 ISCC 版本（既没有文件版本资源，也不在卸载注册表里），跳过版本门禁。'
}

Remove-Tree $DistDir

& $iscc "/DAppVersion=$appVersion" "/DAppVersionNumeric=$numericVersion" $IssFile
if ($LASTEXITCODE -ne 0) {
    Stop-WithError "Inno Setup 编译失败（退出码 $LASTEXITCODE），请查看上方编译器输出。"
}

# ---------- 结果 ----------
$installers = @(Get-ChildItem -LiteralPath $DistDir -Filter '*.exe' -File)
if ($installers.Count -eq 0) {
    Stop-WithError "编译已结束，但 $DistDir 下没有找到 .exe 产物。"
}

Write-Step '打包完成'
foreach ($installer in $installers) {
    $hash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash
    Write-Detail "$($installer.FullName)"
    Write-Detail "  体积   : $(Format-Size $installer.Length)"
    Write-Detail "  SHA256 : $hash"
    Write-Host ''
    Write-Detail '把这个 exe 发给对方即可。对方双击安装、选择安装位置，无需任何其它环境准备。'
    Write-Detail '对方重装新版本时直接装这个新包就能覆盖升级，配置与聊天数据都会保留。'
}

exit 0
