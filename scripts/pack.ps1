param(
    [string]$Version,
    [switch]$Upload,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "src\Beamcast\Beamcast.csproj"
$publish = Join-Path $root "artifacts\publish"
$release = Join-Path $root "artifacts\release"
$icon = Join-Path $root "src\Beamcast\Assets\Beamcast.ico"
$notes = Join-Path $root "CHANGELOG.md"

if (-not $Version) {
    [xml]$proj = Get-Content $csproj
    $Version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

if (-not $Version) {
    throw "Pass -Version or set <Version> in the csproj."
}

New-Item -ItemType Directory -Force -Path $publish, $release | Out-Null

if (-not $SkipBuild) {
    if (Test-Path $publish) { Get-ChildItem $publish | Remove-Item -Recurse -Force }

    dotnet publish $csproj -c Release -r win-x64 -o $publish --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    $dotnetTools = Join-Path $env:USERPROFILE ".dotnet\tools"
    if (Test-Path $dotnetTools) {
        $env:PATH = "$dotnetTools;$env:PATH"
    }

    $vpk = Get-Command vpk -ErrorAction SilentlyContinue
    if (-not $vpk) {
        throw "Install the Velopack CLI: dotnet tool install -g vpk --version 1.2.0"
    }

    $packArgs = @(
        "pack",
        "--packId", "Beamcast",
        "--packVersion", $Version,
        "--packDir", $publish,
        "--mainExe", "Beamcast.exe",
        "--packTitle", "Beamcast",
        "--icon", $icon,
        "--outputDir", $release,
        "--msi",
        "--noPortable",
        "--instLocation", "PerUser"
    )
    if (Test-Path $notes) {
        $packArgs += @("--releaseNotes", $notes)
    }

    & vpk @packArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }
}

if (-not $Upload) {
    Write-Host "Package at $release"
    return
}

$token = $env:GH_TOKEN
if (-not $token) { throw "Set GH_TOKEN to upload the Release." }

$msi = Get-ChildItem $release -Filter "*.msi" | Select-Object -First 1
if (-not $msi) { throw "MSI not found in $release." }

$repo = $env:BEAMCAST_REPO_URL
if (-not $repo) { $repo = "https://github.com/lbss9/beamcast" }
$repoSlug = $repo.TrimEnd("/") -replace "^https://github.com/", ""

$createArgs = @(
    "release", "create", "v$Version",
    $msi.FullName,
    "--title", "Beamcast $Version",
    "--repo", $repoSlug
)
if (Test-Path $notes) {
    $createArgs += @("--notes-file", $notes)
}

& gh @createArgs
if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }

Write-Host "Release v$Version uploaded."
