[CmdletBinding()]
param(
    [ValidateSet('patch', 'minor', 'major')][string]$Bump = 'patch',
    [switch]$NonInteractive
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# All release edits and the release commit live in a temporary clone. The user's
# checkout and running application are untouched until the remote release is complete.
function Invoke-Git {
    param([string[]]$Arguments)
    $output = & $script:git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed (exit $LASTEXITCODE)." }
    return $output
}
function Invoke-Gh {
    param([string[]]$Arguments)
    $output = & $script:gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments -join ' ') failed (exit $LASTEXITCODE)." }
    return $output
}
function Replace-Version {
    param([string]$File, [string]$Pattern, [string]$Replacement)
    $utf8 = New-Object Text.UTF8Encoding($false)
    $text = [IO.File]::ReadAllText($File, $utf8)
    if ([regex]::Matches($text, $Pattern).Count -ne 1) { throw "Expected one version declaration in $File ($Pattern)." }
    [IO.File]::WriteAllText($File, [regex]::Replace($text, $Pattern, $Replacement), $utf8)
}
function Get-TargetProcesses {
    param([string]$Target)
    foreach ($process in @(Get-Process -Name Koi -ErrorAction SilentlyContinue)) {
        try {
            if ([string]::Equals($process.Path, $Target, [StringComparison]::OrdinalIgnoreCase)) { $process }
        } catch {
            # An inaccessible or unrelated process must never be terminated by name.
            Write-Warning "Cannot inspect process $($process.Id); it will not be stopped."
        }
    }
}
function Stop-TargetProcesses {
    param([string]$Target, [object[]]$Processes)
    foreach ($process in $Processes) {
        if ($process.HasExited) { continue }
        $started = $process.StartTime
        [void]$process.CloseMainWindow()
        if ($process.WaitForExit(5000)) { continue }
        $current = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if ($null -ne $current -and $current.StartTime -eq $started -and
            [string]::Equals($current.Path, $Target, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Warning "Koi did not close within 5 seconds; stopping target PID $($current.Id)."
            Stop-Process -Id $current.Id -Force -ErrorAction Stop
            if (-not $current.WaitForExit(5000)) { throw "Target PID $($current.Id) did not exit." }
        }
    }
}

$repo = $PSScriptRoot
$stage = $null
$phase = 'preflight'
$releaseCommit = $null
$tag = $null
$remotePublished = $false
$restartRequired = $false
$completed = $false
$failure = $null
$restartFailure = $null
$installAttempted = $false
$originalFiles = @{}
$targetExe = Join-Path $repo 'Koi.exe'
$git = (Get-Command git -CommandType Application -ErrorAction Stop).Source
$gh = (Get-Command gh -CommandType Application -ErrorAction Stop).Source

Push-Location $repo
try {
    & (Join-Path $repo 'build.ps1') -CheckEnvironment -NonInteractive
    $top = (Invoke-Git -Arguments @('rev-parse', '--show-toplevel')).Trim()
    if (-not [string]::Equals([IO.Path]::GetFullPath($top), [IO.Path]::GetFullPath($repo), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Run release.ps1 from the root Koi checkout.'
    }
    if (@(Invoke-Git -Arguments @('status', '--porcelain=v1', '--untracked-files=normal')).Count -ne 0) {
        throw 'Release requires a clean working tree, including untracked files. Commit/review changes first.'
    }
    $branch = (Invoke-Git -Arguments @('symbolic-ref', '--quiet', '--short', 'HEAD')).Trim()
    $baseCommit = (Invoke-Git -Arguments @('rev-parse', 'HEAD')).Trim()
    $remote = (Invoke-Git -Arguments @('config', ('branch.' + $branch + '.remote'))).Trim()
    $mergeRef = (Invoke-Git -Arguments @('config', ('branch.' + $branch + '.merge'))).Trim()
    if ($remote -ne 'origin' -or -not $mergeRef.StartsWith('refs/heads/')) {
        throw 'The release branch must track an origin branch. Set its upstream before releasing.'
    }
    $fetchUrl = (Invoke-Git -Arguments @('remote', 'get-url', 'origin')).Trim()
    $pushUrl = (Invoke-Git -Arguments @('remote', 'get-url', '--push', 'origin')).Trim()
    if ($fetchUrl -ne $pushUrl) { throw 'Separate fetch/push URLs are not supported by this release script.' }
    $identityName = (Invoke-Git -Arguments @('config', 'user.name')).Trim()
    $identityEmail = (Invoke-Git -Arguments @('config', 'user.email')).Trim()
    if (-not $identityName -or -not $identityEmail) { throw 'Configure git user.name and user.email first.' }
    $repository = (Invoke-Gh -Arguments @('repo', 'view', '--json', 'nameWithOwner,viewerPermission,url')) | ConvertFrom-Json
    if ($repository.viewerPermission -notin @('ADMIN', 'MAINTAIN', 'WRITE')) { throw 'GitHub repository write permission is required.' }
    $repoName = [string]$repository.nameWithOwner
    $githubHost = ([Uri]$repository.url).Host
    $repoSpec = $githubHost + '/' + $repoName
    Invoke-Gh -Arguments @('auth', 'status', '--hostname', $githubHost) | Out-Host
    # Dry-run checks credentials/ref compatibility; server branch rules may still reject the real push.
    Invoke-Git -Arguments @('push', '--dry-run', 'origin', ('HEAD:' + $mergeRef)) | Out-Host

    $version = ([IO.File]::ReadAllText((Join-Path $repo 'VERSION'))).Trim()
    if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "Invalid VERSION: $version" }
    $major = [int]$Matches[1]; $minor = [int]$Matches[2]; $patch = [int]$Matches[3]
    switch ($Bump) {
        'major' { $major++; $minor = 0; $patch = 0 }
        'minor' { $minor++; $patch = 0 }
        'patch' { $patch++ }
    }
    $next = "$major.$minor.$patch"
    $tag = 'v' + $next
    $localTags = @(Invoke-Git -Arguments @('tag', '--list', $tag))
    if ($localTags.Count -ne 0) { throw "Local tag $tag already exists; inspect the previous release attempt." }
    $remoteTags = @(Invoke-Git -Arguments @('ls-remote', '--tags', 'origin', ('refs/tags/' + $tag)))
    if ($remoteTags.Count -ne 0) { throw "Remote tag $tag already exists; finish that release instead of creating it again." }

    $phase = 'prepare'
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('Koi-release-' + [Guid]::NewGuid().ToString('N'))
    Invoke-Git -Arguments @('clone', '--quiet', '--no-hardlinks', '--no-checkout', '--', $repo, $stage) | Out-Host
    Invoke-Git -Arguments @('-C', $stage, 'checkout', '--quiet', '--detach', $baseCommit) | Out-Host
    Invoke-Git -Arguments @('-C', $stage, 'remote', 'set-url', 'origin', $pushUrl) | Out-Host
    Invoke-Git -Arguments @('-C', $stage, 'config', 'user.name', $identityName) | Out-Host
    Invoke-Git -Arguments @('-C', $stage, 'config', 'user.email', $identityEmail) | Out-Host
    [IO.File]::WriteAllText((Join-Path $stage 'VERSION'), $next + "`n", [Text.Encoding]::ASCII)
    $old = [regex]::Escape($version)
    Replace-Version (Join-Path $stage 'Program.cs') ('AssemblyVersion\("' + $old + '\.0"\)') ('AssemblyVersion("' + $next + '.0")')
    Replace-Version (Join-Path $stage 'Program.cs') ('AssemblyFileVersion\("' + $old + '\.0"\)') ('AssemblyFileVersion("' + $next + '.0")')
    Replace-Version (Join-Path $stage 'DockWindow.cs') ('AppVersion = "' + $old + '"') ('AppVersion = "' + $next + '"')

    $phase = 'test-and-build'
    & (Join-Path $stage 'test.ps1') -NonInteractive
    & (Join-Path $stage 'build.ps1') -OutputPath (Join-Path $stage 'Koi.exe') -NonInteractive
    # Only the explicit release files are staged, never unrelated working-tree content.
    $releaseFiles = @('VERSION', 'Program.cs', 'DockWindow.cs', 'Koi.exe', 'Koi.exe.sha256')
    Invoke-Git -Arguments (@('-C', $stage, 'add', '--') + $releaseFiles) | Out-Host
    Invoke-Git -Arguments @('-C', $stage, 'commit', '-m', ('release ' + $tag)) | Out-Host
    $releaseCommit = (Invoke-Git -Arguments @('-C', $stage, 'rev-parse', 'HEAD')).Trim()
    Invoke-Git -Arguments @('-C', $stage, 'tag', $tag) | Out-Host

    $phase = 'push'
    # Branch and tag move together, or neither moves. No force push or remote deletion.
    Invoke-Git -Arguments @('-C', $stage, 'push', '--atomic', 'origin', ('HEAD:' + $mergeRef), ('refs/tags/' + $tag)) | Out-Host
    $phase = 'create-draft'
    Push-Location $stage
    try {
        # Draft first: a failed upload never advertises an incomplete public release.
        Invoke-Gh -Arguments @('release', 'create', $tag, '--repo', $repoSpec, '--verify-tag', '--draft', '--title', $tag, '--generate-notes') | Out-Host
        $phase = 'upload-assets'
        Invoke-Gh -Arguments @('release', 'upload', $tag, (Join-Path $stage 'Koi.exe'), (Join-Path $stage 'Koi.exe.sha256'), '--repo', $repoSpec) | Out-Host
        $phase = 'publish-release'
        Invoke-Gh -Arguments @('release', 'edit', $tag, '--repo', $repoSpec, '--draft=false') | Out-Host
        $remotePublished = $true
    } finally { Pop-Location }

    $phase = 'install-local'
    if ((Invoke-Git -Arguments @('rev-parse', 'HEAD')).Trim() -ne $baseCommit -or
        @(Invoke-Git -Arguments @('status', '--porcelain=v1', '--untracked-files=normal')).Count -ne 0) {
        throw 'The original checkout changed during release; published files were not installed locally.'
    }
    # Fetch the exact staged commit, without relying on a possibly stale remote-tracking branch.
    Invoke-Git -Arguments @('fetch', '--no-tags', $stage, ('refs/tags/' + $tag + ':refs/tags/' + $tag)) | Out-Host
    foreach ($name in $releaseFiles) {
        $original = Join-Path $repo $name
        $originalFiles[$name] = $null
        if (Test-Path -LiteralPath $original) { $originalFiles[$name] = [IO.File]::ReadAllBytes($original) }
    }
    $running = @(Get-TargetProcesses $targetExe)
    $restartRequired = $running.Count -gt 0
    Stop-TargetProcesses $targetExe $running
    $installAttempted = $true
    Invoke-Git -Arguments @('merge', '--ff-only', $releaseCommit) | Out-Host
    $completed = $true
} catch {
    $failure = $_
    if ($installAttempted) {
        try {
            # A failed checkout can leave partial file writes without advancing HEAD.
            # Restore only bytes attributable to this candidate; preserve concurrent edits.
            if ((Invoke-Git -Arguments @('rev-parse', 'HEAD')).Trim() -eq $baseCommit) {
                foreach ($name in $originalFiles.Keys) {
                    $destination = Join-Path $repo $name
                    if (-not (Test-Path -LiteralPath $destination)) { continue }
                    $current = [Convert]::ToBase64String([IO.File]::ReadAllBytes($destination))
                    $candidate = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $stage $name)))
                    if ($current -eq $candidate) {
                        if ($null -eq $originalFiles[$name]) { Remove-Item -LiteralPath $destination -Force }
                        else { [IO.File]::WriteAllBytes($destination, $originalFiles[$name]) }
                    }
                }
            }
        } catch { Write-Warning "Could not restore uncommitted install files: $($_.Exception.Message)" }
    }
} finally {
    if ($restartRequired) {
        try {
            if (@(Get-TargetProcesses $targetExe).Count -eq 0) {
                $restarted = Start-Process -FilePath $targetExe -WorkingDirectory $repo -PassThru
                Start-Sleep -Milliseconds 700
                if ($restarted.HasExited) { throw 'Koi exited immediately after restart.' }
            }
        } catch { $restartFailure = $_; $completed = $false }
    }
    Pop-Location
}

if (-not $completed) {
    $message = "Release did not complete. Phase: $phase."
    if ($remotePublished) { $message += ' The GitHub release is published, but local installation/restart needs attention.' }
    if ($failure) { $message += "`n" + $failure.Exception.Message }
    if ($restartFailure) { $message += "`nRestart failed: " + $restartFailure.Exception.Message }
    if ($stage -and (Test-Path -LiteralPath $stage)) {
        $recovery = @(
            $message,
            "Staging checkout retained: $stage",
            "Original checkout: $repo",
            "Tag: $tag",
            "Release commit: $releaseCommit",
            'The original checkout was not used to prepare the release. No commit/tag was reset or deleted.',
            'Inspect git status in both checkouts and the remote tag/release before retrying.',
            'If push failed, retry the atomic branch/tag push from the staging checkout.',
            'If asset upload failed, upload Koi.exe and Koi.exe.sha256 to the existing draft, then publish it.',
            'If publication succeeded, close the original Koi instance and fast-forward the original checkout to the release commit.',
            'See docs/testing.md for recovery commands. Do not bump the version again to recover a partial release.'
        ) -join "`r`n"
        $artifactDir = Join-Path $repo 'artifacts'
        [IO.Directory]::CreateDirectory($artifactDir) | Out-Null
        [IO.File]::WriteAllText((Join-Path $artifactDir 'release-recovery.txt'), $recovery, (New-Object Text.UTF8Encoding($false)))
        Write-Warning $recovery
    }
    throw $message
}

try { Remove-Item -LiteralPath $stage -Recurse -Force }
catch { Write-Warning "Release succeeded, but the temporary checkout could not be removed: $stage" }
Write-Host "Release $tag published with Koi.exe and SHA-256; local checkout updated."
