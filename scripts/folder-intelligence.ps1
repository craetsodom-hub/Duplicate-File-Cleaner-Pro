[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    Write-Host '> Phase 19 Duplicate Folders and Master Folder comparison suite'
    & dotnet test tests\DuplicateFileCleanerPro.Core.Tests\DuplicateFileCleanerPro.Core.Tests.csproj --configuration Release --filter 'FullyQualifiedName~FolderIntelligenceTests|FullyQualifiedName~ArchitectureBoundaryTests' --logger 'console;verbosity=detailed'
    if ($LASTEXITCODE -ne 0) { throw "Folder intelligence core suite failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }
