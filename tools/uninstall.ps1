#requires -Version 2.0
<# MPlot 停用/卸载：只改名，不删除任何文件。必须保存为 UTF-8 with BOM。 #>
$ErrorActionPreference = 'Continue'
# Win7 的 PowerShell 2.0 用系统 ANSI 代码页输出中文，不要强制 UTF-8。
if ($PSVersionTable -and $PSVersionTable.PSVersion.Major -ge 3) { try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { } }

$Roots = @(
    (Join-Path (Join-Path $env:ProgramFiles 'Autodesk\ApplicationPlugins') 'MPlot.bundle'),
    (Join-Path (Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins') 'MPlot.bundle')
)
$Targets = @($Roots | Where-Object { Test-Path -LiteralPath $_ })

Write-Host ''
if ($Targets.Count -eq 0) {
    Write-Host '  没有找到已安装的 MPlot.bundle，无需停用。' -ForegroundColor Yellow
    foreach ($r in $Roots) { Write-Host ('  检查过：' + $r) -ForegroundColor DarkGray }
    exit 0
}

Write-Host '  找到以下安装位置：' -ForegroundColor White
foreach ($t in $Targets) { Write-Host ('    · ' + $t) -ForegroundColor DarkGray }
Write-Host '  操作方式：仅把文件夹改名，文件一个都不会删除。' -ForegroundColor DarkGray
Write-Host '  恢复方法：把改名后的文件夹改回 MPlot.bundle 即可。' -ForegroundColor DarkGray
Write-Host ''
$ans = Read-Host '  确认停用请输入 Y，直接回车取消'
if ($ans -ne 'Y' -and $ans -ne 'y') {
    Write-Host '  已取消，未做任何改动。' -ForegroundColor Yellow
    exit 0
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$okCount = 0
$failCount = 0
foreach ($t in $Targets) {
    $dest = Join-Path (Split-Path -Parent $t) ('MPlot.bundle.disabled-' + $stamp)
    try {
        Move-Item -LiteralPath $t -Destination $dest -ErrorAction Stop
        Write-Host ('  [OK] 已停用 → ' + $dest) -ForegroundColor Green
        $okCount++
    }
    catch {
        Write-Host ('  [X] 停用失败：' + $t) -ForegroundColor Red
        Write-Host ('      ' + $_.Exception.Message) -ForegroundColor DarkGray
        Write-Host '      若是权限不足：请先关闭 AutoCAD/天正，再右键「卸载.bat」→ 以管理员身份运行。' -ForegroundColor Yellow
        $failCount++
    }
}

Write-Host ''
if ($okCount -gt 0) {
    Write-Host '  下次启动 AutoCAD / 天正时不会再加载「K-批量打印」。' -ForegroundColor Green
}
Write-Host '  想彻底删除：关闭 CAD 后，在资源管理器里手动删除改名后的目录即可。' -ForegroundColor DarkGray
# L3：本脚本只改名，不清 TRUSTEDPATHS 里的条目、不删按需加载键、不清 CUI 菜单。
# 如需彻底清理，请在 CAD 里运行 MP_UNINSTALL，或手工清理上述残留项。
Write-Host '  注意：受信任位置（TRUSTEDPATHS）里的条目与 CUI 里的菜单本脚本未清理；' -ForegroundColor DarkGray
Write-Host '        彻底清理请在 CAD 里运行 MP_UNINSTALL。' -ForegroundColor DarkGray
Write-Host ''
if ($failCount -gt 0) { exit 1 }
exit 0
