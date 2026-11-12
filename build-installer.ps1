<#
.SYNOPSIS
    Publishes AutoBright (framework-dependent) and builds the MSI installer.

.DESCRIPTION
    1. Publishes the app framework-dependent: the .NET Desktop Runtime is NOT
       bundled (keeps the payload small), while the Windows App SDK IS bundled
       (so the only external dependency is the .NET Desktop Runtime).
    2. Builds installer/AutoBright.Installer.wixproj, harvesting that publish
       folder into an MSI. The MSI checks for the .NET Desktop Runtime and guides
       the user to install it if missing.

    Requires only the .NET SDK on PATH; the WiX build SDK is restored from NuGet
    automatically by `dotnet build`.

.EXAMPLE
    ./build-installer.ps1
    ./build-installer.ps1 -Version 1.3.0.0
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Rid          = 'win-x64',
    [string]$Version      = '1.2.0.0'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$project    = Join-Path $root 'src/AutoBright/AutoBright.csproj'
$wixproj    = Join-Path $root 'installer/AutoBright.Installer.wixproj'
$publishDir = Join-Path $root 'artifacts/publish'

Write-Host "==> Publishing $Rid (framework-dependent; .NET runtime NOT bundled)..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

# --self-contained false overrides the csproj's self-contained default, so the
# .NET runtime is left out. WindowsAppSDKSelfContained=true keeps the Windows App
# SDK bundled, so the runtime is the single external prerequisite.
dotnet publish $project `
    -c $Configuration `
    -r $Rid `
    --self-contained false `
    -p:WindowsAppSDKSelfContained=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$sizeMB = [math]::Round((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "    Published $sizeMB MB to $publishDir" -ForegroundColor DarkGray

Write-Host "==> Building MSI (v$Version)..." -ForegroundColor Cyan
# Force a clean rebuild: WiX's incremental build keys off the .wxs/.wixproj, not
# the contents of the harvested publish folder, so without this it would skip
# re-harvesting when only the payload changed.
foreach ($d in @('installer/bin', 'installer/obj')) {
    $p = Join-Path $root $d
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}
dotnet build $wixproj `
    -c $Configuration `
    -t:Rebuild `
    -p:ProductVersion=$Version `
    -p:AppPublishDir=$publishDir
if ($LASTEXITCODE -ne 0) { throw "MSI build failed ($LASTEXITCODE)" }

$msi = Get-ChildItem (Join-Path $root 'installer/bin') -Recurse -Filter '*.msi' |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msi) { throw "No .msi was produced under installer/bin." }

Write-Host ""
Write-Host "==> Done. Installer: $($msi.FullName)" -ForegroundColor Green
