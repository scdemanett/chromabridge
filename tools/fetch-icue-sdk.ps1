# Downloads Corsair's iCUE SDK v4 client library from Corsair's GitHub release and places it
# where the Corsair provider project expects it. The library is not kept in git.
$ErrorActionPreference = 'Stop'

$version = '4.0.84'
$zipSha256 = 'eb4414cf505145f3e507dc839ad587bf7ca684d64be3d9a834f416a136736d5d'
$url = "https://github.com/CorsairOfficial/cue-sdk/releases/download/v$version/iCUESDK_$version.zip"

$target = Join-Path $PSScriptRoot '..\src\ChromaBridge.Providers.Corsair\native'
$work = Join-Path ([IO.Path]::GetTempPath()) "icue-sdk-$version"
$zip = "$work.zip"

Invoke-WebRequest -Uri $url -OutFile $zip

$actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()

if ($actual -ne $zipSha256) {
    Remove-Item $zip
    throw "The download does not match the expected SHA-256. Expected $zipSha256, got $actual."
}

Expand-Archive -Path $zip -DestinationPath $work -Force
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item (Join-Path $work 'iCUESDK\redist\x64\iCUESDK.x64_2019.dll') $target -Force

Remove-Item $zip
Remove-Item $work -Recurse -Force

"iCUE SDK $version placed in $((Resolve-Path $target).Path)"
