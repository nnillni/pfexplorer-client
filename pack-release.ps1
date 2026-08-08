# Local pre-release sanity check: builds the plugin in Release config and
# reports the version that would be tagged/published. Doesn't publish
# anything itself — pushing a `vX.Y.Z.W` tag matching PfExplorer.csproj's
# Version does that (see .github/workflows/release.yml), which builds fresh
# in CI, updates repo.json, and attaches latest.zip to a GitHub Release.
# Run this first just to confirm the build succeeds and see what version
# you're about to tag.

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$dotnet = Join-Path $env:LOCALAPPDATA "dotnet-sdks\net10\dotnet.exe"
$env:DOTNET_ROOT = Join-Path $env:LOCALAPPDATA "dotnet-sdks\net10"

Push-Location $root
try {
    & $dotnet build -c Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
}
finally {
    Pop-Location
}

$builtManifest = Join-Path $root "bin\Release\PfExplorer\PfExplorer.json"
$manifest = Get-Content $builtManifest -Raw | ConvertFrom-Json

Write-Host ""
Write-Host "Build OK — version $($manifest.AssemblyVersion)"
Write-Host "To publish: git tag v$($manifest.AssemblyVersion) && git push origin v$($manifest.AssemblyVersion)"
