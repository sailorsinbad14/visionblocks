# 打包 视搭 VisionBlocks 的两种发布物 / Builds the two release artifacts:
#   1) 便携文件夹 zip：自包含（带 .NET 运行时），解压双击即用，数据放在旁边的 data\（靠 portable.txt 认出来）
#   2) 单个 exe：自包含 + 单文件 + 压缩，数据放「文档\VisionBlocks」，设置放 %AppData%\VisionBlocks
# 用法 / Usage: powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-Version 0.2.0]
# 需要 .NET 10 SDK；第一次运行会从 nuget.org 下载 Microsoft.NETCore.App.Runtime.win-x64 和 Microsoft.WindowsDesktop.App.Runtime.win-x64。
# Needs the .NET 10 SDK; the first run downloads the win-x64 runtime packs from nuget.org.
param([string]$Version = "0.2.0")
$ErrorActionPreference = "Stop"
$vb = Split-Path -Parent $PSScriptRoot
$skill = Split-Path -Parent $vb
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
# 和 tools/env.sh 一样：仓库上一级有 .nuget\packages（开发时的实验环境）就把缓存放那里，否则用 dotnet 默认位置
if (Test-Path "$skill\.nuget\packages") {
    $env:DOTNET_CLI_HOME = "$skill\.dotnet-home"; $env:NUGET_PACKAGES = "$skill\.nuget\packages"
    $env:NUGET_HTTP_CACHE_PATH = "$skill\.nuget\http-cache"; $env:NUGET_PLUGINS_CACHE_PATH = "$skill\.nuget\plugins-cache"
}
$proj = "$vb\src\VisionBlocks.Studio\VisionBlocks.Studio.csproj"
$out = "$vb\out\package"
$name = "VisionBlocks-$Version-win-x64"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory $out | Out-Null
$common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:DebugType=none", "-p:DebugSymbols=false", "-p:Version=$Version", "-nologo")

"== 便携文件夹"
$pdir = "$out\$name-portable"
& dotnet publish $proj @common -o $pdir
if ($LASTEXITCODE -ne 0) { throw "便携版发布失败" }
@"
这个文件在，VisionBlocks 就把你的程序、运行结果和设置都放在旁边的 data 文件夹里。整个文件夹拷走，数据就一起带走。
删掉这个文件，数据会改存到「文档\VisionBlocks」。

While this file exists, VisionBlocks keeps your programs, results and settings in the data folder next to it.
Copy the whole folder to take your data with you. Delete this file to store data in Documents\VisionBlocks instead.
"@ | Set-Content -Encoding UTF8 "$pdir\portable.txt"
@"
视搭 VisionBlocks $Version（便携版 / portable）

双击 VisionBlocksStudio.exe 启动。不用安装 .NET。需要 Windows 10 2004 或更新版本。
「读文字」「文字包含」用的是 Windows 自带的中文 OCR：没装中文（简体）语言时，编辑器启动后会在「活动」里提示。
第一次启动时，示例程序会放到 data\programs\示例\；点顶上的「载入示例」打开巡逻示例，在 ① 里「启动靶子」就能试跑。
升级：解压新版，把旧文件夹里的 data 文件夹拷过去。

Double-click VisionBlocksStudio.exe. No .NET install needed. Requires Windows 10 2004 or later.
Text reading uses the built-in Windows OCR; if Chinese (Simplified) is not installed, the editor says so in Activity on start.
On first start, sample programs are placed in data\programs\示例\. To upgrade, unzip the new version and copy the old data folder over.
"@ | Set-Content -Encoding UTF8 "$pdir\README.txt"
# 结果格式说明（给读取运行结果的其他程序看）。编辑器启动时也会在 data\results\ 里放一份
Copy-Item "$vb\docs\results-format.zh.md" "$pdir\结果格式说明.md"
Copy-Item "$vb\docs\results-format.en.md" "$pdir\results-format.en.md"
Copy-Item "$vb\LICENSE" "$pdir\LICENSE.txt"
Copy-Item "$vb\THIRD-PARTY-NOTICES.md" "$pdir\THIRD-PARTY-NOTICES.md"
Compress-Archive -Path $pdir -DestinationPath "$out\$name-portable.zip" -CompressionLevel Optimal

"== 单个 exe"
$sdir = "$out\single"
& dotnet publish $proj @common -o $sdir -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "单文件发布失败" }
Copy-Item "$sdir\VisionBlocksStudio.exe" "$out\$name.exe"

# 每个包旁边放一份 .sha256（和 sha256sum 同格式：哈希、两个空格、文件名），下载的人可以核对
foreach ($f in @("$out\$name-portable.zip", "$out\$name.exe")) {
    $h = (Get-FileHash $f -Algorithm SHA256).Hash.ToLower()
    [IO.File]::WriteAllText("$f.sha256", "$h  $(Split-Path -Leaf $f)`n")
}

"== 结果"
Get-ChildItem $out -File | ForEach-Object { "{0,-45} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
"便携文件夹解开后：{0:N1} MB，{1} 个文件" -f ((Get-ChildItem $pdir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), (Get-ChildItem $pdir -Recurse -File).Count
"单文件目录里除 exe 外的文件：" + ((Get-ChildItem $sdir -File | Where-Object Name -ne "VisionBlocksStudio.exe" | ForEach-Object Name) -join ", ")
