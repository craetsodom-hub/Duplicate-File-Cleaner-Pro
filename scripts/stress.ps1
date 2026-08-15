[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host '> Phase 10 unattended stress suite (temporary corpora are removed by the test)'
    & dotnet test tests\DuplicateFileCleanerPro.IntegrationTests\DuplicateFileCleanerPro.IntegrationTests.csproj --configuration Release -p:Platform=x64 --filter 'TestCategory=Stress' --logger 'console;verbosity=detailed'
    if ($LASTEXITCODE -ne 0) { throw "Stress suite failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }
