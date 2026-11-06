# Publishes ChromaBridge into artifacts\publish: the helper, the provider host and the tray
# app in one folder, self-contained, so the target machine needs no .NET install.
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$output = Join-Path $root 'artifacts\publish'

if (-not (Test-Path (Join-Path $root 'src\ChromaBridge.Providers.Corsair\native\iCUESDK.x64_2019.dll'))) {
    throw 'The iCUE SDK library is missing. Run tools\fetch-icue-sdk.ps1 first.'
}

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

foreach ($project in 'ChromaBridge.Service', 'ChromaBridge.Host', 'ChromaBridge.App') {
    dotnet publish (Join-Path $root "src\$project\$project.csproj") -c $Configuration -r win-x64 --self-contained true -o $output -nologo -v:quiet -p:DebugType=none

    if ($LASTEXITCODE -ne 0) {
        throw "Publishing $project failed."
    }
}

Copy-Item (Join-Path $root 'LICENSE.txt') $output

$size = (Get-ChildItem $output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
"Published to $output ({0:N0} MB)" -f $size
