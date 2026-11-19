#requires -Version 2.0
<#
  MPlot「K-批量打印」一键安装器  ——  Windows PowerShell 2.0+ 兼容（Win7 SP1 原生 PowerShell 2.0）
  ------------------------------------------------------------------
  做四件事：
    1) 自动找到天正所依附的 AutoCAD（注册表 + 常见目录扫描）
    2) 用本机已有的 C# 编译器现场编译（不需要 Visual Studio / SDK / 联网 / NuGet）
    3) 部署到 %PROGRAMFILES%\Autodesk\ApplicationPlugins\MPlot.bundle
     —— 这是 Autodesk 官方唯一「默认受信任、不检查数字签名」的位置；
        装到 %APPDATA% 那个位置，未签名 DLL 在 SECURELOAD=1/2 下会弹安全警告甚至不加载。
     安装位置需要管理员权限，脚本会自动弹 UAC；拿不到权限才退回 %APPDATA% 并给出明确后续操作。
  4) 写好 PackageContents.xml 与安装日志，旧版本先备份不删除

  ★ 本文件必须保存为 UTF-8 with BOM，否则 Windows PowerShell 会把中文按 GBK 读。
#>
param([string]$AcadDir = '', [switch]$UserInstall, [switch]$CompileOnly)

$ErrorActionPreference = 'Continue'
# Win7 的 PowerShell 2.0 跑在 .NET 2.0 上，控制台按系统 ANSI 代码页输出中文才不乱码。
if ($PSVersionTable -and $PSVersionTable.PSVersion.Major -ge 3) {
    try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
    try { $OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
}

# $PSScriptRoot 是 PowerShell 3.0 才有的；Win7 的 PS2 用 $MyInvocation 兜底。
$scriptDir = ''
if ($PSScriptRoot) { $scriptDir = $PSScriptRoot }
else { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
$Root      = Split-Path -Parent $scriptDir
$Src       = Join-Path $Root 'src'
$Build     = Join-Path $Root 'build'
$LogPath   = Join-Path $Root 'install.log'
$UserPlugins = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$Target    = Join-Path (Join-Path $env:ProgramFiles 'Autodesk\ApplicationPlugins') 'MPlot.bundle'
$FallbackUser = $false
$script:logLines = New-Object 'System.Collections.Generic.List[string]'
$script:utf8bom  = New-Object System.Text.UTF8Encoding -ArgumentList $true
$script:fwDir    = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $script:fwDir 'csc.exe'))) {
    # Win7 32 位只有 Framework，没有 Framework64：回退到 32 位原生编译器。
    $script:fwDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$script:chosenDir = ''

function IsBlank {
    param([string]$s)
    return ($null -eq $s -or $s.Trim().Length -eq 0)
}
function New-Obj {
    param([hashtable]$Props)
    $o = New-Object PSObject
    foreach ($k in $Props.Keys) { $o | Add-Member -MemberType NoteProperty -Name $k -Value $Props[$k] }
    return $o
}
function Say {
    param([string]$m = '', [string]$c = 'Gray')
    Write-Host $m -ForegroundColor $c
    $script:logLines.Add($m)
}
function SaveLog {
    try { [System.IO.File]::WriteAllLines($LogPath, $script:logLines.ToArray(), $script:utf8bom) } catch { }
}
function Die {
    param([string]$m)
    Say ''
    Say ('[X] ' + $m) 'Red'
    Say ''
    Say ('详细日志：' + $LogPath) 'Yellow'
    Say '排错见 README.md 第 11 节「故障速查」；完整报错在 install.log，请把该文件发给开发者。' 'Yellow'
    SaveLog
    exit 1
}

# 编译失败时打印指定编译器输出里的全部报错行（H4 教训：以前只显示第一遍的最后 30 行，
# Roslyn 那遍完全不显示，错误藏在截断之外）。匹配不到 error 时改打最后 10 行，绝不一片空白。
function Show-Errors {
    param([string]$label, [string]$text)
    $lines = @($text -split "`r?`n")
    $errs = @($lines | Where-Object { $_ -match 'error' })
    Say ('   --- ' + $label + '：共 ' + $errs.Count + ' 个报错行 ---') 'Yellow'
    if ($errs.Count -gt 0) {
        foreach ($l in $errs) { Say ('   ' + $l) 'DarkGray' }
    } else {
        foreach ($l in @($lines | Select-Object -Last 10)) { Say ('   ' + $l) 'DarkGray' }
    }
}

# ------------------------------------------------------------------ 1. 找 AutoCAD
function Get-Major {
    param([string]$d)
    # ★ 不能信 acad.exe 的 FileMajorPart：本机 AutoCAD 2014 的 FileVersion 是
    #   "R19.1.18.0.0"，.NET FileVersionInfo 会把它解析成 FileMajorPart = 25，
    #   于是 2014 被误判成 AutoCAD 2025，安装器会跑去要 .NET 8 SDK 而直接失败。
    #   托管程序集版本才是準的：本机 acdbmgd.dll = 19.1.0.0 -> major 19。
    $db = Join-Path $d 'acdbmgd.dll'
    if (Test-Path -LiteralPath $db) {
        try {
            $v = [System.Reflection.AssemblyName]::GetAssemblyName($db).Version
            if ($v -and $v.Major -ge 17 -and $v.Major -le 40) { return [int]$v.Major }
        } catch { }
    }
    $exe = Join-Path $d 'acad.exe'
    if (Test-Path -LiteralPath $exe) {
        try {
            $fv = [string](Get-Item -LiteralPath $exe).VersionInfo.FileVersion
            $m = [regex]::Match($fv, 'R(\d{2})\.')
            if ($m.Success) { return [int]$m.Groups[1].Value }
        } catch { }
    }
    return 0
}
function Test-AcadDir {
    param([string]$d)
    if ((IsBlank $d)) { return $false }
    $a = Join-Path $d 'acdbmgd.dll'
    $b = Join-Path $d 'acmgd.dll'
    return ((Test-Path -LiteralPath $a) -and (Test-Path -LiteralPath $b))
}
function Add-Cand {
    param($list, [string]$d, [string]$rel)
    if ((IsBlank $d)) { return }
    $d = ([string]$d).TrimEnd('\')
    foreach ($x in $list) { if ($x.Dir -ieq $d) { return } }
    $o = New-Obj @{
        Dir     = $d
        Release = $rel
        Major   = (Get-Major $d)
        HasCore = (Test-Path -LiteralPath (Join-Path $d 'accoremgd.dll'))
    }
    $list.Add($o) | Out-Null
}
function Find-Acad {
    $list = New-Object 'System.Collections.Generic.List[object]'
    $bases = @(
        'HKLM:\SOFTWARE\Autodesk\AutoCAD',
        'HKLM:\SOFTWARE\WOW6432Node\Autodesk\AutoCAD',
        'HKCU:\SOFTWARE\Autodesk\AutoCAD'
    )
    foreach ($b in $bases) {
        if (-not (Test-Path -LiteralPath $b)) { continue }
        foreach ($rk in @(Get-ChildItem -LiteralPath $b -ErrorAction SilentlyContinue)) {
            $rel = $rk.PSChildName
            foreach ($pk in @(Get-ChildItem -LiteralPath $rk.PSPath -ErrorAction SilentlyContinue)) {
                $pr = Get-ItemProperty -LiteralPath $pk.PSPath -ErrorAction SilentlyContinue
                if ($null -eq $pr) { continue }
                foreach ($n in @('Location', 'InstallDir', 'AcadLocation')) {
                    $v = $pr.$n
                    if ($v) { Add-Cand $list ([string]$v) $rel }
                }
            }
        }
    }
    # 磁盘扫描只是注册表扫描的兜底：枚举本机全部固定盘符（L8 教训：以前只写死 C:-F:，
    # CAD 装在 G: 盘及更靠后盘符、且注册表又读不到时会被漏掉）。
    $drives = @()
    try {
        foreach ($di in [System.IO.DriveInfo]::GetDrives()) {
            if ($di.DriveType -eq 'Fixed' -and $di.IsReady) { $drives += $di.Name.TrimEnd('\') }
        }
    } catch { }
    if ($drives.Count -eq 0) { $drives = @('C:', 'D:', 'E:', 'F:') }
    foreach ($drv in $drives) {
        $pats = @(
            "$drv\Program Files\Autodesk\AutoCAD*",
            "$drv\Program Files (x86)\Autodesk\AutoCAD*",
            "$drv\Autodesk\AutoCAD*",
            "$drv\Program Files\Tangent\AutoCAD*",
            "$drv\TArch\AutoCAD*"
        )
        foreach ($pat in $pats) {
            foreach ($d in @(Get-ChildItem -Path $pat -ErrorAction SilentlyContinue | Where-Object { $_.PSIsContainer })) {
                if (Test-AcadDir $d.FullName) { Add-Cand $list $d.FullName '(scan)' }
            }
        }
    }
    return $list
}

Say '============================================================' 'Cyan'
Say ' MPlot「K-批量打印」一键安装' 'Cyan'
Say (' 工程目录：' + $Root) 'DarkGray'
Say '============================================================' 'Cyan'
Say ''

# ------------------------------------------------------------------ 0. 权限 / 受信任位置
# 未签名的 DLL 只有放在 %PROGRAMFILES%\Autodesk\ApplicationPlugins 才默认受信任。
# 没有管理员权限时自动退回用户目录，并在最后给出必须做的那一步。
if ($CompileOnly) {
    # 只做「按本机 AutoCAD 程序集现场编译」验证：不提权、不部署、不改系统目录。
    $Target = Join-Path $Build 'CompileOnly'
    Say '[0/5] 编译验证模式（-CompileOnly）：不部署、不修改任何 CAD 目录。' 'Cyan'
}
elseif ($UserInstall) {
    $Target = Join-Path $UserPlugins 'MPlot.bundle'
    $FallbackUser = $true
    Say '[0/5] 按参数指定，安装到当前用户目录。' 'DarkGray'
}
else {
    $isAdmin = $false
    try {
        $pr = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        $isAdmin = $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { }
    if ($isAdmin) {
        Say '[0/5] 已具备管理员权限，安装到 Autodesk 默认受信任目录。' 'DarkGray'
    }
    else {
        Say '[0/5] 安装到 Autodesk 受信任目录需要管理员权限，正在弹出 UAC ...' 'Yellow'
        $bat = Join-Path $Root '一键安装.bat'
        $elevated = $false
        try {
            if ($AcadDir) {
                $argline = '-AcadDir "' + $AcadDir + '"'
                Start-Process -FilePath $bat -Verb RunAs -ArgumentList $argline -ErrorAction Stop | Out-Null
            }
            else {
                Start-Process -FilePath $bat -Verb RunAs -ErrorAction Stop | Out-Null
            }
            $elevated = $true
        } catch { }
        if ($elevated) {
            Say '  已在新窗口以管理员身份继续安装，请到新窗口查看结果。' 'Cyan'
            Say '  （本窗口可以直接关闭）' 'DarkGray'
            SaveLog
            # 退出码 2 = 已转交管理员窗口：真正的安装还没开始，绝不能 exit 0，
            # 否则一键安装.bat 会误报 [OK]（L2 教训）。
            exit 2
        }
        Say '  ! 自动提权失败，改用当前用户目录安装，最后会告诉你需要补做的一步。' 'Yellow'
        $Target = Join-Path $UserPlugins 'MPlot.bundle'
        $FallbackUser = $true
    }
}

Say '[1/5] 查找天正依附的 AutoCAD ...'

$cands = @(Find-Acad)
$cands = @($cands | Where-Object { Test-AcadDir $_.Dir })
if ($cands.Count -eq 0 -and (IsBlank $AcadDir)) {
    Die '没找到 AutoCAD 安装目录（未发现 acdbmgd.dll / acmgd.dll）。请确认这台机器装了 AutoCAD / 天正；或右键用管理员运行；或指定目录：一键安装.bat -AcadDir "D:\AutoCAD 2024"'
}
foreach ($c in $cands) {
    Say ('   · ' + $c.Dir + '    major=' + $c.Major + '    accoremgd=' + $c.HasCore) 'DarkGray'
}

if (-not (IsBlank $AcadDir)) {
    if (-not (Test-AcadDir $AcadDir)) {
        Die ('指定目录里没有 acdbmgd.dll / acmgd.dll：' + $AcadDir)
    }
    $chosen = New-Obj @{
        Dir = $AcadDir.TrimEnd('\'); Release = '(手动指定)'; Major = (Get-Major $AcadDir)
        HasCore = (Test-Path -LiteralPath (Join-Path $AcadDir 'accoremgd.dll'))
    }
}
else {
    $majors = @($cands | ForEach-Object { $_.Major } | Sort-Object -Unique)
    if ($majors.Count -gt 1) {
        # H3 教训：多版本共存时绝不能默认挑最新的——2014 会被静默跳过，
        # 装完 CAD 里没菜单。列出来让用户选，默认 2014（天正主力），直接回车即选默认。
        $sorted = @($cands | Sort-Object -Property Major)
        Say '   检测到多个 AutoCAD 版本：' 'Yellow'
        $idx = 0
        $defIdx = $sorted.Count
        foreach ($c in $sorted) {
            $idx++
            $mark = ''
            if ($c.Major -eq 19) { $defIdx = $idx; $mark = '（默认：天正主力）' }
            Say ('     [' + $idx + '] AutoCAD ' + $c.Major + '  ' + $c.Dir + $mark) 'DarkGray'
        }
        $ans = Read-Host '   选哪个版本安装（输入序号，直接回车用默认）'
        $pick = $defIdx
        if ($ans -match '^\d+$') { $pick = [int]$ans }
        if ($pick -lt 1 -or $pick -gt $sorted.Count) { $pick = $defIdx }
        $chosen = $sorted[$pick - 1]
    }
    else {
        # 只有一个版本（或同一版本的多个安装位置）：取最高的，与以前一致。
        $prefer = @($cands | Where-Object { $_.Major -ge 20 -and $_.Major -le 24 } | Sort-Object -Property Major -Descending)
        if ($prefer.Count -gt 0) { $chosen = $prefer[0] }
        else {
            $any = @($cands | Sort-Object -Property Major -Descending)
            $chosen = $any[0]
        }
    }
}
$script:chosenDir = $chosen.Dir
$major = [int]$chosen.Major
if ($major -eq 0) {
    # 读不到版本号是异常状态（程序集损坏或被占用），绝不能猜一个版本继续装（H3 教训：以前默认按 2024）。
    Die '读不到 AutoCAD 程序集版本号（acdbmgd.dll 可能损坏或被占用），无法判断版本，安装中止。请确认 CAD 能正常启动后重跑。'
}
if ($major -le 18) {
    Die ('检测到 AutoCAD 2012 或更早（.NET 3.5，没有 accoremgd.dll）。本插件需要 AutoCAD 2013 及以上；本机 2014 R19.1 已实测通过。')
}
$isNet8 = ($major -ge 25)
Say ('   使用：' + $chosen.Dir) 'Green'
if ($isNet8) {
    Say '   该版本是 AutoCAD 2025+（.NET 8），走实验通道，需要本机 .NET 8 SDK。' 'Yellow'
} elseif ($major -eq 19) {
    Say '   AutoCAD 2013/2014（.NET 4.0 / CLR 4）：本机 2014 R19.1 已按真实程序集核对并编译通过。' 'Green'
}

# ------------------------------------------------------------------ 2. 准备编译
Say ''
Say '[2/5] 准备编译器与源码 ...'
if (-not (Test-Path -LiteralPath (Join-Path $Src 'Commands.cs'))) {
    Die ('源码目录不完整，找不到 Commands.cs：' + $Src)
}
if (-not (Test-Path -LiteralPath $Build)) { New-Item -ItemType Directory -Path $Build -Force | Out-Null }
$outDll = Join-Path $Build 'MPlot.dll'

function Find-Roslyn {
    $pats = @(
        'C:\Program Files\Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\Roslyn\csc.exe',
        'C:\Program Files\Microsoft Visual Studio\2019\*\MSBuild\Current\Bin\Roslyn\csc.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2019\*\MSBuild\Current\Bin\Roslyn\csc.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2017\*\MSBuild\15.0\Bin\Roslyn\csc.exe',
        'C:\Program Files (x86)\MSBuild\14.0\Bin\Roslyn\csc.exe'
    )
    foreach ($p in $pats) {
        foreach ($f in @(Get-ChildItem -Path $p -ErrorAction SilentlyContinue | Where-Object { -not $_.PSIsContainer })) { return $f.FullName }
    }
    return ''
}

function Invoke-Build48 {
    param([string]$exe, [string]$lang, [string]$dll, [string]$plat)
    $refs = @()
    foreach ($n in @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Data.dll',
                     'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xml.dll',
                     'Microsoft.CSharp.dll')) {
        $p = Join-Path $script:fwDir $n
        if (Test-Path -LiteralPath $p) { $refs += ('/r:' + $p) }
    }
    foreach ($n in @('accoremgd.dll', 'acdbmgd.dll', 'acmgd.dll')) {
        $p = Join-Path $script:chosenDir $n
        if (Test-Path -LiteralPath $p) { $refs += ('/r:' + $p) }
    }
    foreach ($n in @('mscorlib.dll', 'System.dll', 'Microsoft.CSharp.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $script:fwDir $n))) {
            return New-Obj @{ Code = -1; Output = ('缺少 .NET Framework 程序集 ' + $n + '：' + $script:fwDir) }
        }
    }
    foreach ($n in @('acdbmgd.dll', 'acmgd.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $script:chosenDir $n))) {
            return New-Obj @{ Code = -1; Output = ('缺少 AutoCAD 程序集 ' + $n + '：' + $script:chosenDir) }
        }
    }

    $files = @(Get-ChildItem -LiteralPath $script:SrcDir -Filter '*.cs' -Recurse -ErrorAction SilentlyContinue |
               Where-Object { -not $_.PSIsContainer -and $_.FullName -notmatch '\\(bin|obj)\\' } |
               ForEach-Object { $_.FullName })
    if ($files.Count -lt 15) { return New-Obj @{ Code = -1; Output = '源码 .cs 文件不足：' + $files.Count } }

    # /utf8output：让编译器用 UTF-8 输出中文报错，否则 Windows 控制台会把错误说明压成乱码。
    # Win7 自带的是 .NET 4.0 的 C# 4 编译器，不认 /langversion:5；默认即 C# 4。
    $langArgs = @()
    if (-not (IsBlank $lang)) { $langArgs = @(('/langversion:' + $lang)) }
    $all = @('/nologo', '/noconfig', '/utf8output', '/target:library',
             ('/platform:' + $plat), '/optimize+', '/nostdlib+') +
           $langArgs + @(('/out:' + $dll), '/define:TRACE') + $refs + $files
    $raw = & $exe $all 2>&1
    $rc = $LASTEXITCODE
    return New-Obj @{ Code = $rc; Output = ($raw | Out-String) }
}
$script:SrcDir = $Src

# ------------------------------------------------------------------ 3. 编译
Say ''
Say '[3/5] 现场编译 MPlot.dll（不联网、不装任何东西）...'
$built = $false
$buildNote = ''

if ($isNet8) {
    $dn = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dn) {
        Die 'AutoCAD 2025+ 需要 .NET 8 SDK 才能编译插件，本机没有 dotnet。请安装 .NET 8 SDK 后重跑，或改用 AutoCAD 2015-2024。'
    }
    $sdks = (& dotnet --list-sdks 2>&1 | Out-String)
    Say ('   已装 SDK：' + ($sdks.Trim() -replace "`r`n", ' | ')) 'DarkGray'
    if ($sdks -notmatch '(?m)^(8|9|10)\.') {
        Die '本机 dotnet SDK 版本低于 8.0。请安装 .NET 8 SDK 后重跑，或改用 AutoCAD 2015-2024。'
    }
    $proj = Join-Path $Src 'MPlot.Net8.csproj'
    if (-not (Test-Path -LiteralPath $proj)) { Die ('缺少工程文件：' + $proj) }
    $raw = & dotnet build $proj '-c' 'Release' '--nologo' `
              ('-p:AcadDir=' + $chosen.Dir) `
              ('-p:OutputPath=' + $Build + '\') 2>&1
    $rc = $LASTEXITCODE
    $txt = ($raw | Out-String)
    $script:logLines.Add('--- dotnet build ---')
    $script:logLines.Add($txt)
    if ($rc -ne 0 -or -not (Test-Path -LiteralPath $outDll)) {
        Say ($txt | Select-Object -First 1) 'DarkGray'
        Die 'net8.0 编译失败（AutoCAD 2025 迁移改过少量 API 签名）。完整报错在 install.log；也可先用 AutoCAD 2015-2024。'
    }
    $built = $true
    $buildNote = 'net8.0-windows / AutoCAD 2025+ 实验通道'
}
else {
    $csc = Join-Path $script:fwDir 'csc.exe'
    if (-not (Test-Path -LiteralPath $csc)) {
        Die ('系统缺少 .NET Framework 编译器：' + $csc + '。请修复 .NET Framework 4.x 安装。')
    }
    Say ('   编译器：' + $csc) 'DarkGray'
    # 2015+ 全是 64 位；2013/2014 存在 32 位版本，用 AnyCPU 让 CLR 自己决定
    $plat = 'x64'
    if ($major -lt 20) { $plat = 'anycpu' }
    Say ('   目标平台：' + $plat) 'DarkGray'
    $r1 = Invoke-Build48 $csc '' $outDll $plat
    $script:logLines.Add('--- csc 默认语言版本（Win7 / .NET 4.0 C# 4） ---')
    $script:logLines.Add($r1.Output)
    if ($r1.Code -eq 0 -and (Test-Path -LiteralPath $outDll)) {
        $built = $true
        $buildNote = '.NET Framework 4.x 原生编译器（Win7 兼容）'
    }
    else {
        Say '   系统自带编译器未通过，试 Roslyn 编译器 ...' 'Yellow'
        $ros = Find-Roslyn
        if ($ros) {
            $r2 = Invoke-Build48 $ros 'latest' $outDll $plat
            $script:logLines.Add('--- Roslyn /langversion:latest ---')
            $script:logLines.Add($r2.Output)
            if ($r2.Code -eq 0 -and (Test-Path -LiteralPath $outDll)) {
                $built = $true
                $buildNote = 'Roslyn / C# latest'
            }
        }
        if (-not $built) {
            Say ''
            Say '编译失败，全部报错行如下（完整输出在 install.log）：' 'Yellow'
            Show-Errors '系统自带编译器' $r1.Output
            if ($ros) { Show-Errors 'Roslyn 编译器' $r2.Output } else { Say '   （本机未找到 Roslyn 编译器，只跑了系统自带编译器。）' 'DarkGray' }
            Die 'C# 编译失败（见上方全部报错行）。'
        }
    }
}

if (-not $built) { Die '未知原因：编译未产出 MPlot.dll' }
$sz = (Get-Item -LiteralPath $outDll).Length
if ($sz -lt 20000) { Die ('MPlot.dll 体积异常（' + $sz + ' 字节），疑似编译产物不完整') }
Say ('   编译成功：MPlot.dll  ' + [math]::Round($sz / 1024, 1) + ' KB  [' + $buildNote + ']') 'Green'

if ($CompileOnly) {
    SaveLog
    Say ''
    Say ('[OK] 按本机 AutoCAD 程序集编译通过：' + $outDll) 'Green'
    Say '     -CompileOnly 只编译不部署，没有改动 AutoCAD / 系统目录。' 'DarkGray'
    Say ('     完整编译日志：' + $LogPath) 'DarkGray'
    Say ''
    exit 0
}

# ------------------------------------------------------------------ 4. 部署
Say ''
Say '[4/5] 部署到 AutoCAD 自动加载目录 ...'
$running = @(Get-Process -Name 'acad', 'acadlt' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Say '   ! 检测到 AutoCAD/天正正在运行；旧 DLL 可能被占用，若下面复制失败请先关掉 CAD 再重跑。' 'Yellow'
}

# H2：另一 ApplicationPlugins 位置若已有同名 MPlot.bundle（另一套 MPlot 或旧版），
# 与本仓库同名同命令（MP_FRAME/MP_GENERAL），双份加载时哪个生效不可预测。
# 先发现、先问用户，绝不静默并存。继续安装不会删除它。
$otherBundle = ''
if ($Target -like ($env:ProgramFiles + '*')) { $otherBundle = Join-Path $UserPlugins 'MPlot.bundle' }
else { $otherBundle = Join-Path (Join-Path $env:ProgramFiles 'Autodesk\ApplicationPlugins') 'MPlot.bundle' }
if (Test-Path -LiteralPath $otherBundle) {
    Say '' 'Yellow'
    Say '   ! 另一位置已存在同名 MPlot.bundle：' 'Yellow'
    Say ('     ' + $otherBundle) 'Yellow'
    $otherDll = Join-Path $otherBundle 'Contents\MPlot.dll'
    if (Test-Path -LiteralPath $otherDll) {
        $oi = Get-Item -LiteralPath $otherDll
        Say ('     其中 DLL：' + [math]::Round($oi.Length / 1024, 1) + ' KB，修改时间 ' + $oi.LastWriteTime) 'DarkGray'
    }
    Say '     它可能是另一套 MPlot（WorkBuddy 版，命令也是 MP_FRAME/MP_GENERAL），' 'Yellow'
    Say '     与本次安装同名同命令，双份加载时哪个生效不可预测。' 'Yellow'
    Say '     建议：先确认它是不是你要保留的另一套插件；继续安装不会删除它。' 'Yellow'
    $ans2 = Read-Host '   是否继续本次安装（Y 继续 / 其他任意键取消）'
    if ($ans2 -ne 'Y' -and $ans2 -ne 'y') { Die '已取消：另一位置的同名 MPlot.bundle 仍在，请先处理它再重跑安装。' }
}

$parent = Split-Path -Parent $Target
if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
if (Test-Path -LiteralPath $Target) {
    $bak = Join-Path $parent ('MPlot.bundle.backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    try {
        Copy-Item -LiteralPath $Target -Destination $bak -Recurse -Force -ErrorAction Stop
        Say ('   旧版本已备份（不删除）：' + $bak) 'DarkGray'
    }
    catch {
        Say ('   旧版本备份失败，但继续尝试覆盖：' + $_.Exception.Message) 'Yellow'
    }
}
# L7：备份只保留最近 2 份，旧的自动清理，避免反复安装堆出一堆目录。
try {
    $backs = @(Get-ChildItem -LiteralPath $parent -ErrorAction SilentlyContinue |
               Where-Object { $_.PSIsContainer -and $_.Name -like 'MPlot.bundle.backup-*' } |
               Sort-Object -Property Name -Descending)
    if ($backs.Count -gt 2) {
        foreach ($old in @($backs | Select-Object -Skip 2)) {
            try { Remove-Item -LiteralPath $old.FullName -Recurse -Force -ErrorAction Stop } catch { }
        }
    }
} catch { }
$contents = Join-Path $Target 'Contents'
try {
    if (-not (Test-Path -LiteralPath $contents)) { New-Item -ItemType Directory -Path $contents -Force | Out-Null }
    Copy-Item -LiteralPath $outDll -Destination (Join-Path $contents 'MPlot.dll') -Force -ErrorAction Stop
}
catch {
    Die ('复制 MPlot.dll 失败：' + $_.Exception.Message + '  —— 请先完全关闭 AutoCAD / 天正再重跑本安装器。')
}

foreach ($f in @('README.md', 'CHANGELOG.md')) {
    $p = Join-Path $Root $f
    if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination (Join-Path $contents $f) -Force -ErrorAction SilentlyContinue }
}
$deps = Join-Path $Build 'MPlot.deps.json'
if ($isNet8 -and (Test-Path -LiteralPath $deps)) {
    Copy-Item -LiteralPath $deps -Destination (Join-Path $contents 'MPlot.deps.json') -Force -ErrorAction SilentlyContinue
}

# 按实际编译目标写 PackageContents.xml：net48 只在 2015-2024 加载，net8 只在 2025+ 加载，
# 避免版本不对时 AutoCAD 弹出加载失败。
$seriesMin = 'R20.0'
$seriesMaxAttr = ' SeriesMax="R24.3"'
$desc = 'AutoCAD 2015 - 2024 64 位 / .NET Framework'
if ($major -eq 19) { $seriesMin = 'R19.0'; $desc = 'AutoCAD 2013/2014 / .NET 4.0（本机已实测 2014 R19.1 + 天正电气 T20 V3）' }
if ($isNet8) { $seriesMin = 'R25.0'; $seriesMaxAttr = ''; $desc = 'AutoCAD 2025+ 64 位 / .NET 8' }
$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AppVersion="1.0.0"
    ProductCode="{9F2B6C41-7A8D-4E35-B1C0-3D6E5A2F8B70}"
    Name="K-批量打印" Description="图框型批量打印到 PDF、图纸目录、PDF 合集" Author="VentLane">
  <CompanyDetails Name="VentLane" />
  <Components Description="$desc">
    <RuntimeRequirements SeriesMin="$seriesMin"$seriesMaxAttr />
    <ComponentEntry AppName="MPlot" ModuleName="./Contents/MPlot.dll" AppDescription="K-批量打印" />
  </Components>
</ApplicationPackage>
"@
try {
    # 写 UTF-8 BOM：中文 Windows 上 AutoCAD 的 XML 解析器 / 记事本都不会把中文读成乱码。
    [System.IO.File]::WriteAllText((Join-Path $Target 'PackageContents.xml'), $xml, $script:utf8bom)
}
catch { Die ('写 PackageContents.xml 失败：' + $_.Exception.Message) }

$info = @(
    'MPlot「K-批量打印」安装信息',
    ('安装时间   : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')),
    ('工程目录   : ' + $Root),
    ('AutoCAD目录: ' + $chosen.Dir),
    ('版本 major : ' + $major),
    ('编译方式   : ' + $buildNote),
    ('目标目录   : ' + $Target),
    '',
    '本目录由 AutoCAD 的 ApplicationPlugins 机制在启动时自动加载，无需 APPLOAD。',
    '卸载/停用：双击 MPlot 根目录的「卸载.bat」（只把本目录改名，不删除文件）。'
)
[System.IO.File]::WriteAllLines((Join-Path $contents 'install-info.txt'), $info, $script:utf8bom)

# ------------------------------------------------------------------ 5. 自检 + 收尾
Say ''
Say '[5/5] 自检 ...'
$ok = $true
foreach ($p in @((Join-Path $Target 'PackageContents.xml'), (Join-Path $contents 'MPlot.dll'))) {
    if (-not (Test-Path -LiteralPath $p)) { Say ('   缺少：' + $p) 'Red'; $ok = $false }
}
# 绝不能把 AutoCAD 自己的 DLL 复制进 bundle（会加载两份 acdbmgd 导致崩溃）
foreach ($n in @('acdbmgd.dll', 'acmgd.dll', 'accoremgd.dll')) {
    $p = Join-Path $contents $n
    if (Test-Path -LiteralPath $p) {
        # 不删文件，只改名停用：加载两份 acdbmgd 会让 AutoCAD 崩溃
        try {
            Move-Item -LiteralPath $p -Destination ($p + '.disabled') -Force -ErrorAction Stop
            Say ('   已停用误带入的 AutoCAD 程序集：' + $n) 'Yellow'
        }
        catch { Say ('   ! ' + $n + ' 未停用，请手工移出 Contents 目录') 'Red'; $ok = $false }
    }
}
if (-not $ok) { Die '部署自检未通过' }

# M3：部署自检补两项便宜的校验（以前只查文件存在，XML 写坏、版本写错、APPAUTOLOAD 被关掉
# 都会以“成功”收场）：
#   1) PackageContents.xml 能解析，且 SeriesMin 覆盖所选 CAD 版本；
#   2) 任一 CAD 配置的 APPAUTOLOAD 若被显式写成 0，警告（bundle 不会自动加载）。
try {
    [xml]$pkgXml = Get-Content -LiteralPath (Join-Path $Target 'PackageContents.xml') -Encoding UTF8
    $smin = [string]$pkgXml.ApplicationPackage.Components.RuntimeRequirements.SeriesMin
    $sminMajor = 0
    if ($smin -match '^R(\d+)\.') { $sminMajor = [int]$Matches[1] }
    if ($sminMajor -eq 0 -or $sminMajor -gt $major) {
        Say ('   ! PackageContents.xml 的 SeriesMin=' + $smin + ' 不覆盖所选 AutoCAD ' + $major + '，请检查版本选择。') 'Red'
        $ok = $false
    }
} catch {
    Say ('   ! PackageContents.xml 解析失败：' + $_.Exception.Message) 'Red'
    $ok = $false
}
$appAutoWarned = $false
foreach ($vk in @(Get-ChildItem -Path 'HKCU:\Software\Autodesk\AutoCAD' -Recurse -ErrorAction SilentlyContinue |
                 Where-Object { $_.PSChildName -eq 'Variables' -or $_.PSChildName -eq 'General' })) {
    try {
        $av = Get-ItemProperty -LiteralPath $vk.PSPath -Name 'APPAUTOLOAD' -ErrorAction SilentlyContinue
        if ($av -and [string]$av.APPAUTOLOAD -eq '0' -and -not $appAutoWarned) {
            Say '   ! 发现某 CAD 配置的 APPAUTOLOAD=0：bundle 不会自动加载。请在 CAD 里把 APPAUTOLOAD 改成 1 后重启。' 'Yellow'
            $appAutoWarned = $true
        }
    } catch { }
}
if (-not $ok) { Die '部署自检未通过（见上方标红项）' }

# ------------------------------------------------------------------ 5b. 用户目录安装：自动加入受信任位置
# 官方只有 %PROGRAMFILES%\Autodesk\ApplicationPlugins 默认受信任；用户目录里的
# 未签名 DLL 在 SECURELOAD=1/2 下会弹警告甚至不加载。这里直接把该目录追加到
# 当前用户所有 AutoCAD 配置的 TRUSTEDPATHS（HKCU，不需要管理员权限），
# 避免"装完了还得手工去选项里加信任路径"。原值记进 install.log，随时可回滚。
# H1 教训：以前只写 Profiles\<配置>\Variables 键、值是 bundle 的父目录，而卸载器却去
# General 键里按 bundle 目录本身删，两边永远对不上。真机 ground truth（到底哪个键）
# 尚未确认（V1），因此改成两个键都写、值用 bundle 目录本身（更精确：DLL 就在它下面）；
# 卸载器同步改成两个键、两个值都清，哪个键是对的都能闭环。待 V1 确认后可收窄到一个键。
$autoTrusted = $false
if ($FallbackUser) {
    Say ''
    Say '[5b] 把用户插件目录加入 AutoCAD 受信任位置 ...' 'Cyan'
    $trustedRoot = $Target
    $changed = 0
    $already = 0
    $tpKeys = @(Get-ChildItem -Path 'HKCU:\Software\Autodesk\AutoCAD' -Recurse -ErrorAction SilentlyContinue |
                 Where-Object { $_.PSChildName -eq 'Variables' -or $_.PSChildName -eq 'General' })
    foreach ($vk in $tpKeys) {
        try {
            $cur = ''
            $p = Get-ItemProperty -LiteralPath $vk.PSPath -Name 'TRUSTEDPATHS' -ErrorAction SilentlyContinue
            if ($p) { $cur = [string]$p.TRUSTEDPATHS }
            if ($cur -like ('*' + $trustedRoot + '*')) { $already++; continue }
            $newVal = $trustedRoot
            if (-not (IsBlank $cur)) { $newVal = $cur.TrimEnd(';') + ';' + $trustedRoot }
            Set-ItemProperty -LiteralPath $vk.PSPath -Name 'TRUSTEDPATHS' -Value $newVal -ErrorAction Stop
            $script:logLines.Add(('TRUSTEDPATHS 追加：' + $vk.Name + '   原值=[' + $cur + ']   新值=[' + $newVal + ']'))
            $changed++
        } catch {
            Say ('   ! 该配置写入跳过：' + $vk.Name) 'Yellow'
        }
    }
    if (($changed + $already) -gt 0) {
        Say ('   TRUSTEDPATHS：新写入 ' + $changed + ' 个、已存在 ' + $already + ' 个 CAD 配置（原值已存入 install.log）。') 'Green'
        $autoTrusted = $true
    } else {
        Say '   没有找到可写的 CAD 配置；若 CAD 弹出安全提示，点【加载】即可。' 'Yellow'
    }
}

SaveLog
Say ''
Say '============================================================' 'Green'
Say ' [OK] 安装完成' 'Green'
Say '============================================================' 'Green'
Say ''
Say ' 接下来只做一件事：完全关闭并重新打开天正 / AutoCAD。' 'White'
Say ''
Say '   · 顶部菜单栏会出现「K-批量打印」' 'White'
Say '   · 找不到菜单栏：命令行输入 MENUBAR 回车，再输入 1' 'White'
Say '   · 也可以直接敲命令：MP_FRAME（图框信息库 / 批打）' 'White'
Say ('   · 安装位置：' + $Target) 'DarkGray'
Say ('   · 安装日志：' + $LogPath) 'DarkGray'
Say '   · 若菜单仍未出现，命令行输入 APPAUTOLOAD 看是否为 1；为 0 请改成 1 后重启。' 'DarkGray'
if ($isNet8) { Say '   · 注意：AutoCAD 2025+ 是实验通道，若菜单没出现请看 install.log。' 'Yellow' }
if ($FallbackUser) {
    if ($autoTrusted) {
        Say ''
        Say '  [OK] 装在「当前用户目录」，已自动加入 CAD 受信任位置：无需 UAC、无需手工设置。' 'Green'
        Say '       若首次启动仍弹出「文件加载 - 安全问题」，点一次【加载】即可。' 'DarkGray'
        Say '       更省事的做法：右键「一键安装.bat」→ 以管理员身份运行，装到官方默认受信任目录。' 'DarkGray'
    } else {
        Say ''
        Say '  [重要] 这次装在「当前用户目录」，未签名 DLL 需要补做一步：' 'Yellow'
        Say '     打开 AutoCAD/天正 → OP(选项) → 文件 → 受信任位置 → 添加下面这个目录：' 'Yellow'
        Say ('       ' + (Split-Path -Parent $Target)) 'White'
        Say '     或者：启动 CAD 弹出「文件加载 - 安全问题」时点【加载】。' 'Yellow'
        Say '     想完全免掉这一步：右键「一键安装.bat」→ 以管理员身份运行。' 'Yellow'
    }
}
Say ''
exit 0
