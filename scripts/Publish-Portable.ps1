# Builds a self-contained publish folder (and optional zip) for end users.
# Requires .NET 8 SDK only on the machine that runs this script - not on players' PCs.
param(
    [string] $OutputDir = (Join-Path $PSScriptRoot "..\artifacts\R-Util"),
    [switch] $Zip
)

$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "..\RobloxUtility\RobloxUtility.csproj" | Resolve-Path

dotnet publish $proj `
    -c Release `
    -r win-x64 `
    -p:PublishSingleFile=true `
    -p:SelfContained=true `
    -o $OutputDir

Write-Host "Published to: $OutputDir"
Write-Host "Distribute the entire folder (all files) - WPF may place native DLLs next to the EXE."

if ($Zip) {
    $zipPath = "$OutputDir.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path $OutputDir -DestinationPath $zipPath
    Write-Host ('Zip: ' + $zipPath)
}
