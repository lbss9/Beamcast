param(
    [string]$Version,
    [switch]$Upload,
    [switch]$SkipBuild
)

# Builds the app, packs it with Velopack and (with -Upload) publishes a complete update feed to
# GitHub Releases: releases.win.json + full/delta .nupkg + Setup.exe + the MSI. Installed apps
# find updates only through releases.win.json, so uploading the MSI alone is not a release.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "src\Beamcast\Beamcast.csproj"
$publish = Join-Path $root "artifacts\publish"
$release = Join-Path $root "artifacts\release"
$icon = Join-Path $root "src\Beamcast\Assets\Beamcast.ico"
$changelog = Join-Path $root "CHANGELOG.md"
$notes = Join-Path $root "artifacts\release-notes.md"

if (-not $Version) {
    [xml]$proj = Get-Content $csproj
    $Version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) {
    throw "Pass -Version or set <Version> in the csproj."
}

$repo = $env:BEAMCAST_REPO_URL
if (-not $repo) { $repo = "https://github.com/lbss9/Beamcast" }
$repo = $repo.TrimEnd("/")
$repoSlug = $repo -replace "^https://github.com/", ""
$token = $env:GH_TOKEN

New-Item -ItemType Directory -Force -Path $publish, $release | Out-Null

$dotnetTools = Join-Path $env:USERPROFILE ".dotnet\tools"
if (Test-Path $dotnetTools) {
    $env:PATH = "$dotnetTools;$env:PATH"
}
$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
    throw "Install the Velopack CLI: dotnet tool install -g vpk --version 1.2.0"
}

# Release notes: only this version's section of the changelog, so the in-app update window and the
# GitHub release show what changed now, not the whole history.
function Write-ReleaseNotes {
    if (-not (Test-Path $changelog)) { return $false }
    $lines = Get-Content $changelog
    $section = New-Object System.Collections.Generic.List[string]
    $inside = $false
    foreach ($line in $lines) {
        if ($line -match '^## ') {
            if ($inside) { break }
            if ($line -match "^## \s*v?$([regex]::Escape($Version))\s*$") { $inside = $true }
            continue
        }
        if ($inside) { $section.Add($line) }
    }
    $text = ($section -join "`n").Trim()
    if (-not $text) { return $false }
    Set-Content -Path $notes -Value $text -Encoding UTF8
    return $true
}
$hasNotes = Write-ReleaseNotes

if (-not $SkipBuild) {
    if (Test-Path $publish) { Get-ChildItem $publish | Remove-Item -Recurse -Force }
    if (Test-Path $release) { Get-ChildItem $release | Remove-Item -Recurse -Force }

    dotnet publish $csproj -c Release -r win-x64 -o $publish --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    # Previous release next to the new one lets vpk build a delta package; without it every
    # update is a full download. Not fatal: a first release has nothing to download.
    $downloadArgs = @("download", "github", "--repoUrl", $repo, "--outputDir", $release)
    if ($token) { $downloadArgs += @("--token", $token) }
    & vpk @downloadArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not download the previous release; packing without a delta."
        Get-ChildItem $release | Remove-Item -Recurse -Force
    }

    $packArgs = @(
        "pack",
        "--packId", "Beamcast",
        "--packVersion", $Version,
        "--packDir", $publish,
        "--mainExe", "Beamcast.exe",
        "--packTitle", "Beamcast",
        "--packAuthors", "Beamcast",
        "--icon", $icon,
        "--outputDir", $release,
        "--msi",
        "--noPortable",
        "--instLocation", "PerUser"
    )
    if ($hasNotes) {
        $packArgs += @("--releaseNotes", $notes)
    }

    & vpk @packArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }
}

$feed = Join-Path $release "releases.win.json"
$nupkg = Get-ChildItem $release -Filter "Beamcast-$Version-full.nupkg" | Select-Object -First 1
$setup = Get-ChildItem $release -Filter "*-Setup.exe" | Select-Object -First 1
$msi = Get-ChildItem $release -Filter "*.msi" | Select-Object -First 1
foreach ($required in @($feed, $nupkg, $setup, $msi)) {
    if (-not $required -or -not (Test-Path $required)) { throw "Packing did not produce all release files (feed, full nupkg, Setup.exe, MSI) in $release." }
}
Write-Host "Release files:"
Get-ChildItem $release | ForEach-Object { Write-Host ("  {0,-40} {1,10:N0} KB" -f $_.Name, ($_.Length / 1KB)) }

if (-not $Upload) {
    Write-Host "Package at $release"
    return
}

if (-not $token) { throw "Set GH_TOKEN to upload the Release." }

# vpk publishes the feed, the packages and Setup.exe as one GitHub release and keeps the feed
# consistent with earlier releases. The MSI is attached afterwards for people who prefer it.
$uploadArgs = @(
    "upload", "github",
    "--repoUrl", $repo,
    "--token", $token,
    "--outputDir", $release,
    "--publish",
    "--merge",
    "--tag", "v$Version",
    "--releaseName", "Beamcast $Version"
)
& vpk @uploadArgs
if ($LASTEXITCODE -ne 0) { throw "vpk upload github failed." }

& gh release upload "v$Version" $msi.FullName --repo $repoSlug --clobber
if ($LASTEXITCODE -ne 0) { throw "MSI upload failed." }

if ($hasNotes) {
    & gh release edit "v$Version" --repo $repoSlug --notes-file $notes --title "Beamcast $Version"
    if ($LASTEXITCODE -ne 0) { throw "gh release edit failed." }
}

Write-Host "Release v$Version uploaded with update feed."
