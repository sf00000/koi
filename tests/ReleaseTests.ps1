# Isolated release transaction tests. All git/gh/process calls are in-memory fakes.
# The real release script runs only against disposable fixture files.
[CmdletBinding()]
param([switch]$NonInteractive)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$releaseSource = Join-Path (Split-Path -Parent $PSScriptRoot) 'release.ps1'
$failures = 0

function Assert-Release {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}
function New-FakeProcess {
    param([string]$Path)
    $process = [pscustomobject]@{ Id = 4242; Path = $Path; StartTime = [DateTime]::UtcNow; HasExited = $false }
    $process | Add-Member ScriptMethod CloseMainWindow {
        [void]$global:KoiReleaseTestState.Events.Add('stop')
        $this.HasExited = $true
        return $true
    }
    $process | Add-Member ScriptMethod WaitForExit { param($Milliseconds) return $this.HasExited }
    return $process
}

# Shadow dependency discovery, so even a machine with working GitHub credentials
# can never reach the real git/gh commands while this test runs.
function Get-Command {
    param([string]$Name, $CommandType, $ErrorAction)
    if ($Name -eq 'git') {
        return [pscustomobject]@{ Source = {
            $arguments = @($args)
            $state = $global:KoiReleaseTestState
            $global:LASTEXITCODE = 0
            $inStage = $arguments.Count -gt 2 -and $arguments[0] -eq '-C'
            if ($inStage) { $arguments = @($arguments | Select-Object -Skip 2) }
            $verb = $arguments[0]
            switch ($verb) {
                'rev-parse' {
                    if ($arguments[1] -eq '--show-toplevel') { return $state.Root }
                    if ($inStage -and $state.Committed) { return 'release-commit' }
                    if ($state.Installed) { return 'release-commit' }
                    return 'base-commit'
                }
                'status' {
                    if ($state.Scenario -eq 'dirty') { return '?? unrelated.txt' }
                    return
                }
                'symbolic-ref' { return 'main' }
                'config' {
                    switch ($arguments[1]) {
                        'branch.main.remote' { return 'origin' }
                        'branch.main.merge' { return 'refs/heads/main' }
                        'user.name' { if (-not $inStage) { return 'Fixture' }; return }
                        'user.email' { if (-not $inStage) { return 'fixture@example.invalid' }; return }
                        default { throw "Unexpected config: $arguments" }
                    }
                }
                'remote' { if (-not $inStage) { return 'https://github.example/test/koi.git' }; return }
                'ls-remote' { return }
                'clone' {
                    $state.Stage = $arguments[$arguments.Count - 1]
                    Copy-Item -LiteralPath $state.Root -Destination $state.Stage -Recurse
                    return
                }
                'checkout' { return }
                'add' {
                    $expected = @('add', '--', 'VERSION', 'Program.cs', 'DockWindow.cs', 'Koi.exe', 'Koi.exe.sha256') -join '|'
                    Assert-Release (($arguments -join '|') -eq $expected) 'Release staged unexpected files.'
                    return
                }
                'commit' { $state.Committed = $true; return }
                'tag' { if ($arguments[1] -ne '--list') { $state.Tagged = $true }; return }
                'push' {
                    if ($arguments -contains '--dry-run') { return }
                    Assert-Release ($arguments -contains '--atomic') 'Release push must be atomic.'
                    [void]$state.Events.Add('push')
                    if ($state.Scenario -eq 'push') { $global:LASTEXITCODE = 1; return }
                    $state.Pushed = $true
                    return
                }
                'fetch' { return }
                'merge' {
                    Assert-Release ($state.Published) 'Local installation happened before release publication.'
                    [void]$state.Events.Add('merge')
                    if ($state.Scenario -eq 'merge') { $global:LASTEXITCODE = 1; return }
                    if ($state.Scenario -eq 'partial-merge') {
                        foreach ($name in @('VERSION', 'Program.cs', 'Koi.exe', 'Koi.exe.sha256')) {
                            Copy-Item -LiteralPath (Join-Path $state.Stage $name) -Destination (Join-Path $state.Root $name) -Force
                        }
                        $global:LASTEXITCODE = 1
                        return
                    }
                    foreach ($name in @('VERSION', 'Program.cs', 'DockWindow.cs', 'Koi.exe', 'Koi.exe.sha256')) {
                        Copy-Item -LiteralPath (Join-Path $state.Stage $name) -Destination (Join-Path $state.Root $name) -Force
                    }
                    $state.Installed = $true
                    return
                }
                default { throw "Unexpected git command: $arguments" }
            }
        } }
    }
    if ($Name -eq 'gh') {
        return [pscustomobject]@{ Source = {
            $arguments = @($args)
            $state = $global:KoiReleaseTestState
            $global:LASTEXITCODE = 0
            if ($arguments[0] -eq 'repo') {
                return '{"nameWithOwner":"test/koi","viewerPermission":"WRITE","url":"https://github.example/test/koi"}'
            }
            if ($arguments[0] -eq 'auth') { return }
            Assert-Release ($arguments -contains 'github.example/test/koi') 'Release lost the repository hostname.'
            Assert-Release (([IO.File]::ReadAllText((Join-Path $state.Root 'VERSION'))).Trim() -eq '1.8.6') 'Original version changed before publication.'
            Assert-Release ($state.Process.HasExited -eq ($state.Scenario -eq 'not-running')) 'Application running state changed before publication.'
            switch ($arguments[1]) {
                'create' { $state.Draft = $true; return }
                'upload' {
                    Assert-Release ($state.Draft) 'Assets uploaded before draft creation.'
                    if ($state.Scenario -eq 'upload') { $global:LASTEXITCODE = 1; return }
                    $state.Uploaded = $true
                    return
                }
                'edit' {
                    Assert-Release ($state.Uploaded) 'Release published before successful asset upload.'
                    $state.Published = $true
                    [void]$state.Events.Add('publish')
                    return
                }
                default { throw "Unexpected gh command: $arguments" }
            }
        } }
    }
    throw "Unexpected executable lookup: $Name"
}
function Get-Process {
    param([string]$Name, [int]$Id, $ErrorAction)
    $state = $global:KoiReleaseTestState
    if (-not $state.Process.HasExited) { return $state.Process }
}
function Stop-Process { throw 'Forced termination was not expected in this fixture.' }
function Start-Process {
    param([string]$FilePath, [string]$WorkingDirectory, [switch]$PassThru)
    $state = $global:KoiReleaseTestState
    Assert-Release ($FilePath -eq (Join-Path $state.Root 'Koi.exe')) 'Restarted an unrelated path.'
    [void]$state.Events.Add('restart')
    $state.Process = New-FakeProcess $FilePath
    return $state.Process
}
function Start-Sleep { param($Milliseconds) }

foreach ($scenario in @('dirty', 'test', 'build', 'push', 'upload', 'merge', 'partial-merge', 'success', 'not-running', 'unrelated')) {
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ('Koi-release-test-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    $state = [pscustomobject]@{
        Root = $fixture; Stage = $null; Scenario = $scenario; Committed = $false; Tagged = $false
        Pushed = $false; Draft = $false; Uploaded = $false; Published = $false; Installed = $false
        Events = (New-Object 'System.Collections.Generic.List[string]'); Process = $null
    }
    $state.Process = New-FakeProcess (Join-Path $fixture 'Koi.exe')
    if ($scenario -eq 'not-running') { $state.Process.HasExited = $true }
    if ($scenario -eq 'unrelated') { $state.Process.Path = Join-Path $fixture 'other/Koi.exe' }
    $global:KoiReleaseTestState = $state
    try {
        Copy-Item -LiteralPath $releaseSource -Destination (Join-Path $fixture 'release.ps1')
        [IO.File]::WriteAllText((Join-Path $fixture 'VERSION'), '1.8.6')
        [IO.File]::WriteAllText((Join-Path $fixture 'Program.cs'), '[assembly: AssemblyVersion("1.8.6.0")] [assembly: AssemblyFileVersion("1.8.6.0")]')
        [IO.File]::WriteAllText((Join-Path $fixture 'DockWindow.cs'), 'const string AppVersion = "1.8.6";')
        [IO.File]::WriteAllText((Join-Path $fixture 'Koi.exe'), 'original-binary')
        [IO.File]::WriteAllText((Join-Path $fixture 'build.ps1'), @'
param([switch]$CheckEnvironment, [switch]$NonInteractive, [string]$OutputPath)
if ($CheckEnvironment) { return }
if ($global:KoiReleaseTestState.Scenario -eq 'build') { throw 'Injected build failure' }
[IO.File]::WriteAllText($OutputPath, 'new-binary')
[IO.File]::WriteAllText($OutputPath + '.sha256', 'fixture-checksum')
'@)
        [IO.File]::WriteAllText((Join-Path $fixture 'test.ps1'), @'
param([switch]$NonInteractive)
if ($global:KoiReleaseTestState.Scenario -eq 'test') { throw 'Injected test failure' }
'@)
        $errorSeen = $null
        try { & (Join-Path $fixture 'release.ps1') -NonInteractive 6>$null 3>$null }
        catch { $errorSeen = $_ }
        Assert-Release (($null -eq $errorSeen) -eq ($scenario -in @('success', 'not-running', 'unrelated'))) "Unexpected outcome for $scenario`: $errorSeen"
        Assert-Release ($state.Process.HasExited -eq ($scenario -eq 'not-running')) 'The original running state was not restored.'
        if ($scenario -in @('success', 'not-running', 'unrelated')) {
            Assert-Release ($state.Installed -and $state.Published -and $state.Uploaded) 'Release was incompletely installed/published.'
            Assert-Release (([IO.File]::ReadAllText((Join-Path $fixture 'VERSION'))).Trim() -eq '1.8.7') 'Version was not installed.'
            $expectedEvents = 'push,publish,stop,merge,restart'
            if ($scenario -in @('not-running', 'unrelated')) { $expectedEvents = 'push,publish,merge' }
            Assert-Release (($state.Events -join ',') -eq $expectedEvents) 'Successful transaction order or process ownership changed.'
            Assert-Release (-not (Test-Path -LiteralPath $state.Stage)) 'Successful staging checkout was not cleaned up.'
        } else {
            Assert-Release (([IO.File]::ReadAllText((Join-Path $fixture 'VERSION'))).Trim() -eq '1.8.6') 'Failure changed the original version.'
            Assert-Release ([IO.File]::ReadAllText((Join-Path $fixture 'Koi.exe')) -eq 'original-binary') 'Failure changed the original binary.'
            Assert-Release (-not (Test-Path -LiteralPath (Join-Path $fixture 'Koi.exe.sha256'))) 'Failure left a new checksum in the original checkout.'
            if ($scenario -in @('merge', 'partial-merge')) {
                Assert-Release ($state.Published) 'Merge failure should retain the published release.'
                Assert-Release (($state.Events -join ',') -eq 'push,publish,stop,merge,restart') 'Merge failure did not restore the running instance.'
            } else {
                Assert-Release (-not ($state.Events -contains 'stop')) 'Failure before install stopped the application.'
            }
            if ($scenario -in @('push', 'upload', 'merge', 'partial-merge')) {
                Assert-Release ($state.Committed -and $state.Tagged) 'Prepared release commit/tag was lost.'
                Assert-Release (Test-Path -LiteralPath $state.Stage) 'Failed staging checkout was removed.'
                Assert-Release (Test-Path -LiteralPath (Join-Path $fixture 'artifacts/release-recovery.txt')) 'Recovery record missing.'
            }
        }
        Write-Host "PASS ReleaseTests/$scenario"
    } catch {
        $failures++
        Write-Warning "FAIL ReleaseTests/$scenario`: $_"
    } finally {
        if ($state.Stage -and (Test-Path -LiteralPath $state.Stage)) { Remove-Item -LiteralPath $state.Stage -Recurse -Force }
        if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
        Remove-Variable -Name KoiReleaseTestState -Scope Global -ErrorAction SilentlyContinue
    }
}
if ($failures -gt 0) { throw "$failures isolated release transaction tests failed." }
Write-Host 'All isolated release transaction tests passed; no external git/gh/process commands were used.'
