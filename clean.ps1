param(
    [switch]$WhatIf
)

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectRoot

# Build artifacts to remove, mirroring .gitignore. The virtualenv (.venv) is
# intentionally left alone.
$targets = @(
    'build',
    'dist',
    'buildtest',
    'buildtestdist',
    'autobright.egg-info',
    'autobright.exe'
)

$removed = 0

foreach ($target in $targets) {
    $path = Join-Path $projectRoot $target
    if (Test-Path $path) {
        Write-Host "Removing $target" -ForegroundColor Yellow
        if (-not $WhatIf) {
            Remove-Item -Path $path -Recurse -Force -ErrorAction Stop
        }
        $removed++
    }
}

# Remove __pycache__ directories and stray compiled files anywhere in the
# project tree, excluding the virtualenv.
$cacheDirs = Get-ChildItem -Path $projectRoot -Directory -Recurse -Force -Filter '__pycache__' |
    Where-Object { $_.FullName -notlike "*\.venv\*" }
foreach ($dir in $cacheDirs) {
    Write-Host "Removing $($dir.FullName)" -ForegroundColor Yellow
    if (-not $WhatIf) {
        Remove-Item -Path $dir.FullName -Recurse -Force -ErrorAction Stop
    }
    $removed++
}

$strayFiles = Get-ChildItem -Path $projectRoot -File -Recurse -Force -Include '*.pyc', '*.pyo', '*.pyd' |
    Where-Object { $_.FullName -notlike "*\.venv\*" }
foreach ($file in $strayFiles) {
    Write-Host "Removing $($file.FullName)" -ForegroundColor Yellow
    if (-not $WhatIf) {
        Remove-Item -Path $file.FullName -Force -ErrorAction Stop
    }
    $removed++
}

if ($WhatIf) {
    Write-Host "$removed item(s) would be removed (WhatIf)." -ForegroundColor Cyan
} elseif ($removed -eq 0) {
    Write-Host 'Nothing to clean.' -ForegroundColor Green
} else {
    Write-Host "Clean completed: $removed item(s) removed." -ForegroundColor Green
}
