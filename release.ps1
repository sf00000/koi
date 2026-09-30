# Koi 一键发版脚本
# 用法:
#   powershell -File release.ps1            # 递增补丁位 1.0.0 -> 1.0.1
#   powershell -File release.ps1 minor      # 递增次位   1.0.0 -> 1.1.0 (新功能)
#   powershell -File release.ps1 major      # 递增主位   1.0.0 -> 2.0.0 (大改版)
# 步骤: 递增版本号 -> 同步 VERSION/Program.cs -> 编译 -> 提交 -> 打 tag -> 推送 -> 创建 GitHub Release(附 exe)
param([string]$Bump = "patch")
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSCommandPath
if (-not $repo) { $repo = 'G:\workbuddy\Koi' } # 兜底：极少数宿主取不到脚本路径
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

# 2) 同步版本号到 VERSION 与 Program.cs（显式 UTF-8 读写，防止中文注释被 ANSI 误读损坏）
Set-Content "$repo\VERSION" "$new`n" -Encoding ASCII
$cs = "$repo\Program.cs"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$src = [IO.File]::ReadAllText($cs, $utf8)
$src = $src -replace 'AppVersion = "[0-9.]+"', "AppVersion = `"$new`""
$src = $src -replace 'AssemblyVersion\("[0-9.]+"\)', "AssemblyVersion(`"$new.0`")"
$src = $src -replace 'AssemblyFileVersion\("[0-9.]+"\)', "AssemblyFileVersion(`"$new.0`")"
[IO.File]::WriteAllText($cs, $src, $utf8)

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
if ($LASTEXITCODE -ne 0) { throw "git commit 失败，发版中止" }
git tag "v$new"
if ($LASTEXITCODE -ne 0) { throw "git tag 失败（tag 可能已存在），发版中止" }
git push
if ($LASTEXITCODE -ne 0) { throw "git push 失败，发版中止（本地 commit/tag 已保留，网络恢复后手动 git push && git push origin v$new）" }
git push origin "v$new"
if ($LASTEXITCODE -ne 0) { throw "推送 tag 失败，发版中止" }

# 5) 创建 GitHub Release 并附上 exe
gh release create "v$new" "$repo\Koi.exe" --title "v$new" --generate-notes
if ($LASTEXITCODE -ne 0) { throw "GitHub Release 创建失败（tag 已推送，可手动到 Releases 页面补建）" }

Write-Host "Release v$new 发布完成，启动 Koi..."
Start-Process "$repo\Koi.exe"
