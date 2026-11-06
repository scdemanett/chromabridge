# Authenticode-signs files with the Windows SDK signtool and Microsoft's Azure Artifact
# Signing dlib, as the current Azure CLI session ("az login", or azure/login in CI).
#
# The dlib is not in git. Fetch it once into signing\.artifact-signing:
#   Invoke-WebRequest https://www.nuget.org/api/v2/package/Microsoft.ArtifactSigning.Client/1.0.146 -OutFile client.zip
#   Expand-Archive client.zip -DestinationPath signing\.artifact-signing
#
# The updater only installs releases signed by this identity (see
# src\ChromaBridge.Service\Updates\AuthenticodeVerifier.cs), so an unsigned build can be
# installed by hand but can never be delivered as an update.
param(
    [Parameter(Mandatory)]
    [string[]]$Path
)

$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dlib = Join-Path $root 'signing\.artifact-signing\bin\x64\Azure.CodeSigning.Dlib.dll'
$metadata = Join-Path $root 'signing\artifact-signing.json'

if (-not (Test-Path $dlib)) {
    throw "The Artifact Signing client is missing at $dlib. See the comment at the top of this script."
}

$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if (-not $signtool) {
    throw 'signtool.exe was not found. Install the Windows SDK.'
}

& $signtool.FullName sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib $dlib /dmdf $metadata /d ChromaBridge @Path

if ($LASTEXITCODE -ne 0) {
    throw "signtool failed with exit code $LASTEXITCODE."
}

foreach ($file in $Path) {
    $signature = Get-AuthenticodeSignature $file

    if ($signature.Status -ne 'Valid') {
        throw "$file did not end up with a valid signature ($($signature.Status))."
    }
}
