<#
  纯逻辑实测：把不依赖 AutoCAD 的源文件单独编译成 exe 真跑一遍。
  覆盖 MiniJson（配置持久化）/ Naming（文件命名）/ BuiltInPdfMerge（PDF 合集）/ Preflight helpers（导出前检查辅助）。
  用法：powershell -ExecutionPolicy Bypass -File tools\selftest\run.ps1
  ★ 本文件必须保存为 UTF-8 with BOM。
#>
$ErrorActionPreference = 'Stop'
# Win7 的 PowerShell 2.0 用系统 ANSI 代码页输出中文。
if ($PSVersionTable -and $PSVersionTable.PSVersion.Major -ge 3) { try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { } }

if ($PSScriptRoot) { $here = $PSScriptRoot } else { $here = Split-Path -Parent $MyInvocation.MyCommand.Path }
$root   = Split-Path -Parent (Split-Path -Parent $here)
$src    = Join-Path $root 'src'
$work   = Join-Path $here '_work'
$exe    = Join-Path $work 'selftest.exe'
$csc    = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }

if (-not (Test-Path -LiteralPath $csc)) { Write-Host 'FAIL  找不到系统 C# 编译器' -ForegroundColor Red; exit 1 }
if (-not (Test-Path -LiteralPath $work)) { New-Item -ItemType Directory -Path $work -Force | Out-Null }

Write-Host '=== [A] 生成 3 个标准单页测试 PDF ===' -ForegroundColor Cyan
& python (Join-Path $here 'make_pdfs.py') $work
if ($LASTEXITCODE -ne 0) { Write-Host 'FAIL  测试 PDF 生成失败' -ForegroundColor Red; exit 1 }

Write-Host '=== [B] 编译纯逻辑测试程序（系统自带编译器（Win7 兼容））===' -ForegroundColor Cyan
$files = @(
  (Join-Path $here 'Selftest.cs'),
  (Join-Path $src 'MiniJson.cs'),
  (Join-Path $src 'Models.cs'),
  (Join-Path $src 'Naming.cs'),
  (Join-Path $src 'BuiltInPdfMerge.cs')
)
foreach ($f in $files) { if (-not (Test-Path -LiteralPath $f)) { Write-Host ('FAIL  缺文件 ' + $f) -ForegroundColor Red; exit 1 } }
$args = @('/nologo', '/target:exe', '/platform:anycpu', '/utf8output', ('/out:' + $exe)) + $files
& $csc $args 2>&1 | ForEach-Object { Write-Host ('  ' + $_) }
if (-not (Test-Path -LiteralPath $exe)) { Write-Host 'FAIL  测试程序编译失败' -ForegroundColor Red; exit 1 }
Write-Host ('  OK  selftest.exe  ' + (Get-Item -LiteralPath $exe).Length + ' bytes') -ForegroundColor Green

Write-Host '=== [C] 运行 4 组纯逻辑断言 ===' -ForegroundColor Cyan
& $exe $work 2>&1 | ForEach-Object { Write-Host ('  ' + $_) }
$logicCode = $LASTEXITCODE

Write-Host '=== [D] 独立校验合并产物（xref / 页树 / 内容流）===' -ForegroundColor Cyan
& python (Join-Path $here 'make_pdfs.py') $work
$verifyCode = $LASTEXITCODE

if ($logicCode -eq 0 -and $verifyCode -eq 0) {
  Write-Host '结论：PASS  纯逻辑实测全部通过' -ForegroundColor Green
  exit 0
}
Write-Host ('结论：FAIL  逻辑=' + $logicCode + ' 校验=' + $verifyCode) -ForegroundColor Red
exit 1
