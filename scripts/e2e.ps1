[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host '> Phase 11 unattended Windows workflow E2E suite'
    & dotnet test tests\DuplicateFileCleanerPro.IntegrationTests\DuplicateFileCleanerPro.IntegrationTests.csproj --configuration Release -p:Platform=x64 --filter 'TestCategory!=Stress'
    if ($LASTEXITCODE -ne 0) { throw "E2E suite failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }
