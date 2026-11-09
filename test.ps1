[CmdletBinding()]
param(
    [string]$SourceRoot = $PSScriptRoot,
    [string]$OutputPath,
    [switch]$NonInteractive
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).ProviderPath
if ([string]::IsNullOrEmpty($OutputPath)) { $OutputPath = Join-Path $SourceRoot 'artifacts\Koi.Tests.exe' }
if (-not [IO.Path]::IsPathRooted($OutputPath)) { $OutputPath = Join-Path $SourceRoot $OutputPath }
& (Join-Path $PSScriptRoot 'build.ps1') -SourceRoot $SourceRoot -OutputPath $OutputPath -Tests -NonInteractive
& $OutputPath
if ($LASTEXITCODE -ne 0) { throw "Regression tests failed (exit $LASTEXITCODE)." }
