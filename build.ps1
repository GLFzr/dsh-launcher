# 一键构建 DSH 启动器(Windows PowerShell 5.1+)
# 依赖: .NET Framework 4.x 自带的 csc.exe(Windows 自带, 无需安装 SDK)
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# 产物: dist\DSH-Launcher.exe

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $root 'src'
$outDir = Join-Path $root 'dist'
$outExe = Join-Path $outDir 'DSH-Launcher.exe'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '找不到 csc.exe(.NET Framework 4.x 未安装?)' }

$sources = @(Get-ChildItem $srcDir -Filter *.cs | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "src 目录里没有 .cs 文件: $srcDir" }

New-Item -ItemType Directory -Force $outDir | Out-Null

$argsList = @(
  '/nologo', '/target:winexe', '/optimize+',
  "/out:$outExe",
  '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll'
)
$manifest = Join-Path $root 'app.manifest'
if (Test-Path $manifest) { $argsList += "/win32manifest:$manifest" }
$icon = Join-Path $root 'icon\dsh-launcher.ico'
if (Test-Path $icon) { $argsList += "/win32icon:$icon" }
$argsList += $sources

Write-Host "编译 $($sources.Count) 个源文件 ..."
& $csc @argsList
if ($LASTEXITCODE -ne 0) { throw "编译失败 (csc exit $LASTEXITCODE)" }

Write-Host "OK -> $outExe"
Write-Host "提示: 把 launcher.json(可选)放在 exe 同目录即可改端口/指定 WSL 发行版。"
