[CmdletBinding()]
param(
    [string]$SourceRoot = $PSScriptRoot,
    [string]$OutputPath,
    [switch]$Tests,
    [switch]$CheckEnvironment,
    [switch]$NonInteractive
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([string]::IsNullOrEmpty($env:WINDIR)) {
    throw 'Building Koi requires Windows and the .NET Framework 4.x compiler.'
}
$compilerDir = $null
foreach ($relative in @('Microsoft.NET\Framework64\v4.0.30319', 'Microsoft.NET\Framework\v4.0.30319')) {
    $candidate = Join-Path $env:WINDIR $relative
    if (Test-Path (Join-Path $candidate 'csc.exe')) { $compilerDir = $candidate; break }
}
if (-not $compilerDir) { throw '.NET Framework csc.exe was not found. Install/repair .NET Framework 4.x.' }

$references = @('System.dll', 'System.Core.dll', 'System.Xml.dll')
if (-not $Tests) {
    $references += @('System.Drawing.dll', 'System.Windows.Forms.dll', 'Microsoft.VisualBasic.dll')
    foreach ($assembly in @('PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll')) {
        $assemblyPath = Join-Path $compilerDir ('WPF\' + $assembly)
        if (-not (Test-Path $assemblyPath)) { throw "Missing WPF assembly: $assemblyPath" }
        $references += $assemblyPath
    }
    $xaml = Join-Path $compilerDir 'System.Xaml.dll'
    if (-not (Test-Path $xaml)) {
        $xaml = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll'
    }
    if (-not (Test-Path $xaml)) { throw "Missing System.Xaml.dll: $xaml" }
    $references += $xaml
}
if ($CheckEnvironment) { Write-Host "Compiler ready: $compilerDir\csc.exe"; return }

$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).ProviderPath
if ([string]::IsNullOrEmpty($OutputPath)) {
    $name = 'Koi.exe'
    if ($Tests) { $name = 'Koi.Tests.exe' }
    $OutputPath = Join-Path $SourceRoot ('artifacts\' + $name)
}
if (-not [IO.Path]::IsPathRooted($OutputPath)) { $OutputPath = Join-Path $SourceRoot $OutputPath }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null

if ($Tests) {
    # Keep the regression executable independent of WPF, COM and the live desktop.
    $sourceNames = @('Models.cs', 'PathIdentity.cs', 'StackService.cs', 'LaunchService.cs', 'ConfigStore.cs', 'FolderService.cs')
    $sources = @($sourceNames | ForEach-Object {
        $path = Join-Path $SourceRoot $_
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing test source: $path" }
        $path
    })
    $sources += @(Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'tests') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
} else {
    $sources = @(Get-ChildItem -LiteralPath $SourceRoot -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
}
if ($sources.Count -eq 0) { throw 'No C# source files were found.' }
$target = 'winexe'
if ($Tests) { $target = 'exe' }
$arguments = @('-nologo', '-noconfig', '-langversion:5', '-optimize+', '-warn:4', ('-target:' + $target), ('-out:' + $OutputPath))
$arguments += @($references | ForEach-Object { '-r:' + $_ })
$arguments += $sources

Push-Location $compilerDir
try {
    & (Join-Path $compilerDir 'csc.exe') @arguments
    if ($LASTEXITCODE -ne 0) { throw "C# compilation failed (exit $LASTEXITCODE)." }
} finally { Pop-Location }

$hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = $hash + '  ' + [IO.Path]::GetFileName($OutputPath) + "`n"
[IO.File]::WriteAllText($OutputPath + '.sha256', $checksum, [Text.Encoding]::ASCII)
Write-Host "Built: $OutputPath"
Write-Host "SHA-256: $hash"
