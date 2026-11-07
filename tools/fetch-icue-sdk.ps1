# Places Corsair's iCUE SDK client library beside a development build, where the Corsair
# provider looks first. Installed copies get it from the SDK archive instead (see
# src\ChromaBridge.Providers.Corsair\IcueSdk.cs). The archive comes from tools\fetch-bundles.ps1
# and is pinned in bundles.json; the library is not kept in git.
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'fetch-bundles.ps1') | Out-Null

$bundles = Get-Content (Join-Path $PSScriptRoot '..\bundles.json') -Raw | ConvertFrom-Json
$zip = Join-Path $PSScriptRoot "..\artifacts\bundled\$($bundles.icue.fileName)"
$target = Join-Path $PSScriptRoot '..\src\ChromaBridge.Providers.Corsair\native'
$work = Join-Path ([IO.Path]::GetTempPath()) "icue-sdk-$($bundles.icue.version)"

Expand-Archive -Path $zip -DestinationPath $work -Force
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item (Join-Path $work 'iCUESDK\redist\x64\iCUESDK.x64_2019.dll') $target -Force
Remove-Item $work -Recurse -Force

"iCUE SDK $($bundles.icue.version) placed in $((Resolve-Path $target).Path)"
