[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    Write-Host '> Similar Photos reviewed-removal planner, execution, race, Recycle Bin, presentation, and safety suite'
    & dotnet test tests\DuplicateFileCleanerPro.Core.Tests\DuplicateFileCleanerPro.Core.Tests.csproj --configuration Release --filter 'FullyQualifiedName~SimilarPhotoRemovalTests|FullyQualifiedName~ArchitectureBoundaryTests' --logger 'console;verbosity=detailed'
    if ($LASTEXITCODE -ne 0) { throw "Similar Photos removal core suite failed with exit code $LASTEXITCODE." }
    & dotnet test tests\DuplicateFileCleanerPro.IntegrationTests\DuplicateFileCleanerPro.IntegrationTests.csproj --configuration Release -p:Platform=x64 --filter 'FullyQualifiedName~SimilarPhotoRemovalIntegrationTests|FullyQualifiedName~ArchitectureBoundaryIntegrationTests' --logger 'console;verbosity=detailed'
    if ($LASTEXITCODE -ne 0) { throw "Similar Photos removal Windows suite failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }
