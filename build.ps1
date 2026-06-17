param(
    [switch]$Windowed = $true
)

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectRoot

Write-Host 'Installing packaging dependencies...' -ForegroundColor Cyan
python -m pip install .[packaging]

$pyinstallerArgs = @('--noconfirm')
$pyinstallerArgs += "--distpath=$projectRoot"
$pyinstallerArgs += "--workpath=$projectRoot\build"
# The spec file is configured for a single-file executable, so do not pass onefile/onedir flags.
$pyinstallerArgs += 'autobright.spec'

Write-Host "Running PyInstaller with: $($pyinstallerArgs -join ' ')" -ForegroundColor Cyan
python -m PyInstaller @pyinstallerArgs

if ($LASTEXITCODE -eq 0) {
    Write-Host 'Build completed successfully.' -ForegroundColor Green
} else {
    Write-Host 'PyInstaller build failed.' -ForegroundColor Red
    exit $LASTEXITCODE
}
