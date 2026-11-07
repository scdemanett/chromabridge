# Downloads the third-party archives pinned in bundles.json into artifacts\bundled, checking
# each against its SHA-256. The Full installer ships that folder; the helper uses the same
# pins to download on a Lite install. Archives already present and matching are kept.
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\artifacts\bundled')
)

$ErrorActionPreference = 'Stop'

$bundles = Get-Content (Join-Path $PSScriptRoot '..\bundles.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

foreach ($property in $bundles.PSObject.Properties) {
    if ($property.Name.StartsWith('_')) { continue }

    $bundle = $property.Value
    $target = Join-Path $Destination $bundle.fileName

    if (Test-Path $target) {
        $hash = (Get-FileHash -Path $target -Algorithm SHA256).Hash.ToLowerInvariant()

        if ($hash -eq $bundle.sha256) {
            "$($property.Name) $($bundle.version): already present"
            continue
        }

        Remove-Item $target
    }

    $partial = "$target.download"
    Invoke-WebRequest -Uri $bundle.url -OutFile $partial

    $hash = (Get-FileHash -Path $partial -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($hash -ne $bundle.sha256) {
        Remove-Item $partial
        throw "$($bundle.fileName) does not match its expected SHA-256. Expected $($bundle.sha256), got $hash."
    }

    Move-Item $partial $target -Force
    "$($property.Name) $($bundle.version): downloaded ({0:N1} MB)" -f ((Get-Item $target).Length / 1MB)
}

"Bundles are in $((Resolve-Path $Destination).Path)"
