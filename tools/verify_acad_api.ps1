<#
  MPlot —— 按「本机真实 AutoCAD 程序集」核对插件用到的每一个 API。
  不是照官方文档猜：直接 ReflectionOnlyLoadFrom 本机 acdbmgd/acmgd/accoremgd，
  逐个检查类型、方法、属性、构造、枚举值是否存在。

  用法（在装了 CAD 的机器上，普通 PowerShell 即可，不需要 VS / SDK）：
    powershell -ExecutionPolicy Bypass -File tools\verify_acad_api.ps1
    powershell -ExecutionPolicy Bypass -File tools\verify_acad_api.ps1 -AcadDir "D:\某路径"

  本文件必须保存为 UTF-8 with BOM，否则 Windows PowerShell 会按 GBK 读中文。
#>
param([string]$AcadDir = '')

# PowerShell 2.0（Win7）没有 PS3 用来快速建对象的类型加速器，用这个函数代替。
function New-Obj {
    param([hashtable]$Props)
    $o = New-Object PSObject
    foreach ($k in $Props.Keys) { $o | Add-Member -MemberType NoteProperty -Name $k -Value $Props[$k] }
    return $o
}

$script:AcadDir = $AcadDir
$script:fwDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $script:fwDir 'mscorlib.dll'))) {
    $script:fwDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}

# ReflectionOnly 上下文里解析 System / System.Core / PresentationCore 等依赖
$resolver = [System.ResolveEventHandler]{
    param($sender, $e)
    try {
        $n = New-Object System.Reflection.AssemblyName($e.Name)
        foreach ($dir in @($script:fwDir, $script:AcadDir)) {
            if ([string]::IsNullOrEmpty($dir)) { continue }
            $p = Join-Path $dir ($n.Name + '.dll')
            if (Test-Path -LiteralPath $p) {
                return [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($p)
            }
        }
    } catch { }
    return $null
}
try { [System.AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($resolver) } catch { }

function Find-AcadDir {
    if (-not [string]::IsNullOrEmpty($script:AcadDir)) { return $script:AcadDir }
    $cands = @()
    # 注册表 InstallDir / ProductName 子键
    foreach ($root in @('HKLM:\SOFTWARE\Autodesk\AutoCAD', 'HKLM:\SOFTWARE\WOW6432Node\Autodesk\AutoCAD')) {
        if (Test-Path $root) {
            Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
                $v = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
                if ($v.InstallDir) { $cands += $v.InstallDir }
                Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                    $v2 = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
                    if ($v2.InstallDir) { $cands += $v2.InstallDir }
                }
            }
        }
    }
    foreach ($drive in @('C:', 'D:', 'E:', 'F:')) {
        foreach ($base in @("$drive\Program Files\Autodesk", "$drive\Program Files (x86)\Autodesk")) {
            if (Test-Path -LiteralPath $base) {
                Get-ChildItem -LiteralPath $base -ErrorAction SilentlyContinue | Where-Object { $_.PSIsContainer } | ForEach-Object {
                    if (Test-Path -LiteralPath (Join-Path $_.FullName 'acad.exe')) { $cands += $_.FullName }
                }
                if (Test-Path -LiteralPath (Join-Path $base 'acad.exe')) { $cands += $base }
            }
        }
    }
    foreach ($c in $cands) {
        if (Test-Path -LiteralPath (Join-Path $c 'acdbmgd.dll')) { return $c }
    }
    return ''
}

$script:AcadDir = Find-AcadDir
if ([string]::IsNullOrEmpty($script:AcadDir)) {
    Write-Host 'FAIL  找不到 AutoCAD 安装目录（需要 acdbmgd.dll）。请用 -AcadDir 指定。' -ForegroundColor Red
    exit 2
}
Write-Host ('AutoCAD 目录：' + $script:AcadDir) -ForegroundColor Cyan

$asmList = @()
foreach ($n in @('acdbmgd.dll', 'acmgd.dll', 'accoremgd.dll')) {
    $p = Join-Path $script:AcadDir $n
    if (-not (Test-Path -LiteralPath $p)) { Write-Host ('FAIL  缺少 ' + $n) -ForegroundColor Red; exit 2 }
    $asmList += [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($p)
    Write-Host ('  已加载 ' + $n + '  ' + (Get-Item -LiteralPath $p).Length + ' bytes')
}

$script:pass = 0
$script:fail = 0
$script:failList = @()

function Members($typeName) {
    foreach ($asm in $script:asmList) {
        $t = $asm.GetType($typeName, $false)
        if ($t) {
            $names = @()
            try { $t.GetMethods([System.Reflection.BindingFlags]'Public,Instance,Static') | ForEach-Object { $names += $_.Name } } catch { }
            try { $t.GetProperties([System.Reflection.BindingFlags]'Public,Instance,Static') | ForEach-Object { $names += $_.Name } } catch { }
            try { $t.GetEvents([System.Reflection.BindingFlags]'Public,Instance,Static') | ForEach-Object { $names += $_.Name } } catch { }
            try { $t.GetFields([System.Reflection.BindingFlags]'Public,Instance,Static') | ForEach-Object { $names += $_.Name } } catch { }
            try { $t.GetConstructors([System.Reflection.BindingFlags]'Public,Instance') | ForEach-Object { $names += '.ctor' } } catch { }
            return New-Obj @{ Found = $true; Asm = $asm.GetName().Name; Names = @($names | Select-Object -Unique) }
        }
    }
    return New-Obj @{ Found = $false; Asm = ''; Names = @() }
}

function Check($typeName, $memberList) {
    $m = Members $typeName
    if (-not $m.Found) {
        Write-Host ('  FAIL 类型不存在：' + $typeName) -ForegroundColor Red
        $script:fail++
        $script:failList += ('类型 ' + $typeName)
        return
    }
    foreach ($n in $memberList) {
        if ($m.Names -contains $n) { $script:pass++ }
        else {
            Write-Host ('  FAIL ' + $typeName + ' :: ' + $n + '   (程序集 ' + $m.Asm + ')') -ForegroundColor Red
            $script:fail++
            $script:failList += ($typeName + ' :: ' + $n)
        }
    }
}

function Info($typeName, $filter) {
    $m = Members $typeName
    if (-not $m.Found) { '  (无此类型) ' + $typeName; return }
    ($m.Names | Where-Object { $_ -match $filter } | Sort-Object) -join ', '
}

Write-Host ''
Write-Host '=== 1. 应用程序入口 ===' -ForegroundColor Yellow
# 2014 R19.1 实测：DocumentManager / SetSystemVariable / Idle 都在 Core.Application，
# acmgd 的 Application 只有 COM 入口与对话框 API。写错位置会直接编译失败。
Check 'Autodesk.AutoCAD.ApplicationServices.Application' @('AcadApplication','MenuGroups','MenuBar','LoadPartialMenu','ShowModalDialog','ShowModelessDialog')
Check 'Autodesk.AutoCAD.ApplicationServices.Core.Application' @('DocumentManager','SetSystemVariable','GetSystemVariable','Idle')
Check 'Autodesk.AutoCAD.ApplicationServices.DocumentCollection' @('MdiActiveDocument','DocumentActivated','add_DocumentActivated')

Write-Host ''
Write-Host '=== 2. 打印核心 ===' -ForegroundColor Yellow
Check 'Autodesk.AutoCAD.DatabaseServices.PlotSettings' @('.ctor','CopyFrom','PlotPlotStyles','PrintLineweights','ScaleLineweights','CanonicalMediaName','PlotPaperUnits','Dispose')
Check 'Autodesk.AutoCAD.DatabaseServices.PlotSettingsValidator' @('Current','RefreshLists','SetPlotConfigurationName','SetPlotWindowArea','SetPlotType','SetUseStandardScale','SetStdScaleType','SetCustomPrintScale','SetPlotCentered','SetCurrentStyleSheet','SetPlotPaperUnits','SetPlotOrigin','SetPlotRotation','SetCanonicalMediaName','GetLocaleMediaName','GetCanonicalMediaNameList','GetPlotDeviceList','GetPlotStyleSheetList')
Check 'Autodesk.AutoCAD.DatabaseServices.CustomScale' @('.ctor')
Check 'Autodesk.AutoCAD.DatabaseServices.Extents2d' @('.ctor')
Check 'Autodesk.AutoCAD.DatabaseServices.PlotPaperUnit' @('Millimeters','Inches','Pixels')
Check 'Autodesk.AutoCAD.DatabaseServices.PlotRotation' @('Degrees000','Degrees090')
Check 'Autodesk.AutoCAD.DatabaseServices.StdScaleType' @('ScaleToFit')
Check 'Autodesk.AutoCAD.DatabaseServices.PlotType' @('Window','Layout')
Check 'Autodesk.AutoCAD.PlottingServices.PlotFactory' @('CreatePublishEngine','ProcessPlotState')
Check 'Autodesk.AutoCAD.PlottingServices.PlotEngine' @('BeginPlot','BeginDocument','BeginPage','BeginGenerateGraphics','EndGenerateGraphics','EndPage','EndDocument','EndPlot','Dispose')
Check 'Autodesk.AutoCAD.PlottingServices.PlotInfo' @('.ctor','Layout','OverrideSettings')
Check 'Autodesk.AutoCAD.PlottingServices.PlotPageInfo' @('.ctor')
Check 'Autodesk.AutoCAD.PlottingServices.PlotInfoValidator' @('.ctor','Validate','MediaMatchingPolicy')
Check 'Autodesk.AutoCAD.PlottingServices.MatchingPolicy' @('MatchEnabled')
Check 'Autodesk.AutoCAD.PlottingServices.PlotProgressDialog' @('.ctor','IsVisible','LowerPlotProgressRange','UpperPlotProgressRange','PlotProgressPos','OnBeginPlot','OnBeginSheet','OnEndSheet','OnEndPlot','Dispose')

Write-Host ''
Write-Host '=== 3. 图框 / 属性 / 表格 ===' -ForegroundColor Yellow
Check 'Autodesk.AutoCAD.DatabaseServices.BlockReference' @('Name','DynamicBlockTableRecord','BlockTableRecord','IsDynamicBlock','AttributeCollection','Position','Rotation','ScaleFactors','BlockTransform','Bounds','DynamicBlockReferencePropertyCollection')
Check 'Autodesk.AutoCAD.DatabaseServices.DynamicBlockReferencePropertyCollection' @('Count','GetEnumerator')
Check 'Autodesk.AutoCAD.DatabaseServices.DynamicBlockReferenceProperty' @('Name','Value')
Check 'Autodesk.AutoCAD.DatabaseServices.AttributeReference' @('Tag','TextString','Position','Height','Rotation','Justify','IsMTextAttribute','Invisible')
Check 'Autodesk.AutoCAD.DatabaseServices.AttributeCollection' @('Count','GetEnumerator')
Check 'Autodesk.AutoCAD.DatabaseServices.Table' @('.ctor','InsertRows','Cells','SetTextString','SetRowHeight','SetColumnWidth','Position','Dispose')
Check 'Autodesk.AutoCAD.DatabaseServices.ObjectId' @('IsNull','IsErased','IsValid','ObjectClass')
Check 'Autodesk.AutoCAD.DatabaseServices.Database' @('TransactionManager','CurrentSpaceId','BlockTableId','LayerTableId','Dispose')

Write-Host ''
Write-Host '=== 4. 编辑器 / 交互 ===' -ForegroundColor Yellow
Check 'Autodesk.AutoCAD.EditorInput.Editor' @('WriteMessage','GetPoint','GetCorner','SelectCrossingWindow','SelectWindow','SelectAll','GetSelection','GetEntity','SetImpliedSelection','GetString','ZoomWindow','UpdateScreen','GetCurrentView','SetCurrentView')
Check 'Autodesk.AutoCAD.EditorInput.PromptSelectionResult' @('Status','Value')
Check 'Autodesk.AutoCAD.EditorInput.SelectionFilter' @('.ctor')

Write-Host ''
Write-Host '=== 5. 枚举 / 辅助信息 ===' -ForegroundColor Yellow
Write-Host ('  PlotSettings 的样式与线宽成员：' + (Info 'Autodesk.AutoCAD.DatabaseServices.PlotSettings' 'Style|Lineweight|Scale'))
Write-Host ('  PlotSettingsValidator 全部 Set*：' + (Info 'Autodesk.AutoCAD.DatabaseServices.PlotSettingsValidator' '^Set'))
Write-Host ('  Application(acmgd) 静态属性：' + (Info 'Autodesk.AutoCAD.ApplicationServices.Application' '^get_|^Document|^Idle|^Menu|^Acad'))
Write-Host ('  Application(Core) 静态属性：' + (Info 'Autodesk.AutoCAD.ApplicationServices.Core.Application' '^get_|^Set|^Idle'))

Write-Host ''
Write-Host ('=== 结果：PASS ' + $script:pass + ' / FAIL ' + $script:fail + ' ===') -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
if ($script:fail -gt 0) {
    Write-Host '不存在的成员（需要改用本机可用写法）：' -ForegroundColor Yellow
    $script:failList | ForEach-Object { Write-Host ('  - ' + $_) }
    exit 1
}
Write-Host '本机程序集与 MPlot 代码使用的 API 全部对得上。' -ForegroundColor Green
exit 0
