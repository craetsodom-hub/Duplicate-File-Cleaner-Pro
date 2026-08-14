[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    Write-Host "> dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    Invoke-DotNet @('restore', 'DuplicateFileCleanerPro.sln', '--runtime', 'win-x64')
    Invoke-DotNet @('build', 'DuplicateFileCleanerPro.sln', '--configuration', 'Release', '-p:Platform=x64', '--no-restore')
    Invoke-DotNet @('test', 'DuplicateFileCleanerPro.sln', '--configuration', 'Release', '-p:Platform=x64', '--no-build')
}
finally {
    Pop-Location
}
