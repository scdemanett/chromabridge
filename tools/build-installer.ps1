# Builds the ChromaBridge installers: publish, optionally sign, compile the Inno Setup script
# once per edition. The version comes from src\Directory.Build.props, so that file is the one
# place to bump it.
#
#   tools\build-installer.ps1                  both editions, unsigned, for testing by hand
#   tools\build-installer.ps1 -Edition lite    one edition
#   tools\build-installer.ps1 -Sign            signed; needs an Azure CLI session (see tools\sign.ps1)
#
# Full bundles the third-party archives pinned in bundles.json (OpenRGB, Corsair's iCUE SDK);
# Lite downloads them on first use.
param(
    [ValidateSet('full', 'lite', 'both')]
    [string]$Edition = 'both',
    [switch]$Sign
)

$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')

[xml]$props = Get-Content (Join-Path $root 'src\Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)

if (-not $version) {
    throw 'No <Version> found in src\Directory.Build.props.'
}

$editions = if ($Edition -eq 'both') { 'full', 'lite' } else { , $Edition }

if ($editions -contains 'full') {
    & (Join-Path $PSScriptRoot 'fetch-bundles.ps1')
}

& (Join-Path $PSScriptRoot 'publish.ps1')

$publish = Join-Path $root 'artifacts\publish'

if ($Sign) {
    # Only ChromaBridge's own files; the .NET runtime is Microsoft-signed already and
    # third-party libraries are left as their publishers shipped them
    $own = Get-ChildItem $publish -File | Where-Object { $_.Name -match '^ChromaBridge(\..+)?\.(exe|dll)$' }

    & (Join-Path $PSScriptRoot 'sign.ps1') -Path $own.FullName
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw 'Inno Setup 6 (6.6 or later) was not found.'
}

foreach ($current in $editions) {
    & $iscc /Q "/DAppVersion=$version" "/DEdition=$current" (Join-Path $root 'installer\ChromaBridge.iss')

    if ($LASTEXITCODE -ne 0) {
        throw "The Inno Setup compiler failed with exit code $LASTEXITCODE for the $current edition."
    }

    $installer = Join-Path $root "artifacts\installer\ChromaBridge-$version-$current-setup.exe"

    if ($Sign) {
        & (Join-Path $PSScriptRoot 'sign.ps1') -Path $installer
    }

    "Built $installer ({0:N1} MB){1}" -f ((Get-Item $installer).Length / 1MB), $(if ($Sign) { ', signed' } else { ', unsigned' })
}
