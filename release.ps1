# Koi 一键发版脚本
# 用法:
#   powershell -File release.ps1            # 递增补丁位 1.0.0 -> 1.0.1
#   powershell -File release.ps1 minor      # 递增次位   1.0.0 -> 1.1.0 (新功能)
#   powershell -File release.ps1 major      # 递增主位   1.0.0 -> 2.0.0 (大改版)
# 步骤: 递增版本号 -> 同步 VERSION/Program.cs -> 编译 -> 提交 -> 打 tag -> 推送 -> 创建 GitHub Release(附 exe)
param([string]$Bump = "patch")
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repo

# 1) 读取并递增版本号
$cur = (Get-Content "$repo\VERSION" -Raw).Trim()
$parts = $cur.Split('.')
if ($parts.Count -ne 3) { throw "VERSION 内容非法: $cur" }
$major = [int]$parts[0]; $minor = [int]$parts[1]; $patch = [int]$parts[2]
switch ($Bump) {
  "major" { $major++; $minor = 0; $patch = 0 }
  "minor" { $minor++; $patch = 0 }
  "patch" { $patch++ }
  default { throw "Bump 只支持 patch/minor/major" }
}
$new = "$major.$minor.$patch"
Write-Host "版本: $cur -> $new"

# 2) 同步版本号到 VERSION 与 Program.cs
Set-Content "$repo\VERSION" "$new`n" -Encoding ASCII
$cs = "$repo\Program.cs"
$src = Get-Content $cs -Raw
$src = $src -replace 'AppVersion = "[0-9.]+"', "AppVersion = `"$new`""
$src = $src -replace 'AssemblyVersion\("[0-9.]+"\)', "AssemblyVersion(`"$new.0`")"
$src = $src -replace 'AssemblyFileVersion\("[0-9.]+"\)', "AssemblyFileVersion(`"$new.0`")"
Set-Content $cs $src -Encoding UTF8

# 3) 编译（停止运行中的 Koi 释放 exe）
try { Stop-Process -Name Koi -Force -ErrorAction Stop; Start-Sleep 1 } catch {}
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc -nologo -target:winexe -out:"$repo\Koi.exe" `
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:Microsoft.VisualBasic.dll `
  -r:System.Windows.Forms.dll `
  -r:WPF\PresentationFramework.dll -r:WPF\PresentationCore.dll -r:WPF\WindowsBase.dll `
  -r:"$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll" `
  "$cs"
if ($LASTEXITCODE -ne 0) { throw "编译失败" }
Write-Host "编译完成: Koi.exe"

# 4) 提交 + 打 tag + 推送
git add -A
git commit --no-gpg-sign -m "release v$new" | Out-Null
git tag "v$new"
git push
git push origin "v$new"

# 5) 创建 GitHub Release 并附上 exe
gh release create "v$new" "$repo\Koi.exe" --title "v$new" --generate-notes

Write-Host "Release v$new 发布完成，启动 Koi..."
Start-Process "$repo\Koi.exe"
