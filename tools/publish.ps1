# Publishes ChromaBridge into artifacts\publish: the helper, the provider host and the tray
# app in one folder, self-contained, so the target machine needs no .NET install.
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$output = Join-Path $root 'artifacts\publish'

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
