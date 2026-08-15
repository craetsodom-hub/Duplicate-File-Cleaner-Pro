[CmdletBinding()]
param(
    [switch]$RunWack
)

$ErrorActionPreference = 'Stop'

function Invoke-External {
    param([Parameter(Mandatory = $true)][string]$FilePath, [Parameter(Mandatory = $true)][string[]]$Arguments)

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Get-RequiredTool {
    param([Parameter(Mandatory = $true)][string]$LeafName, [Parameter(Mandatory = $true)][string]$PreferredPath)

    if (Test-Path -LiteralPath $PreferredPath) { return $PreferredPath }
    $candidate = Get-ChildItem -LiteralPath 'C:\Program Files (x86)\Windows Kits\10' -Recurse -File -Filter $LeafName -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $candidate) { throw "Required Windows SDK tool '$LeafName' was not found." }
    return $candidate
}

function Get-PackageVersion {
    param([Parameter(Mandatory = $true)][string]$ManifestPath)

    [xml]$manifest = Get-Content -LiteralPath $ManifestPath -Raw
    return $manifest.Package.Identity.Version
}

function Assert-PackageLayout {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$ExpectedArchitecture,
        [Parameter(Mandatory = $true)][string]$InspectionRoot,
        [Parameter(Mandatory = $true)][string]$MakeAppx
    )

    $destination = Join-Path $InspectionRoot $ExpectedArchitecture
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Invoke-External $MakeAppx @('unpack', '/p', $PackagePath, '/d', $destination, '/o')
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $destination 'AppxManifest.xml') -Raw
    if (-not $manifest.Package.Identity.ProcessorArchitecture.Equals($ExpectedArchitecture, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Package '$PackagePath' was not built for $ExpectedArchitecture."
    }
    if ($manifest.Package.Identity.Name -ne 'DuplicateFileCleanerPro') {
        throw "Package '$PackagePath' has an unexpected identity or version."
    }
    foreach ($runtimeFile in 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll') {
        if (-not (Test-Path -LiteralPath (Join-Path $destination $runtimeFile))) {
            throw "Package '$PackagePath' is missing the self-contained .NET runtime file '$runtimeFile'."
        }
    }
    $forbidden = Get-ChildItem -LiteralPath $destination -Recurse -File |
        Where-Object { $_.FullName -match '\\(\.qa|docs\\design-references|tests|TestResults)\\|\.pfx$|\.cer$|TemporaryQa|Phase\d+\.QA' }
    if ($forbidden) {
        throw "Package '$PackagePath' contains forbidden development material: $($forbidden.FullName -join '; ')"
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src\DuplicateFileCleanerPro.App\DuplicateFileCleanerPro.App.csproj'
$manifest = Join-Path $repositoryRoot 'src\DuplicateFileCleanerPro.App\Package.appxmanifest'
$solution = Join-Path $repositoryRoot 'DuplicateFileCleanerPro.sln'
$artifactRoot = Join-Path $repositoryRoot 'artifacts\store'
$makeAppx = Get-RequiredTool -LeafName 'makeappx.exe' -PreferredPath 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe'
$appCert = 'C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe'
$version = Get-PackageVersion -ManifestPath $manifest
$architectures = @(
    [pscustomobject]@{ Name = 'x64'; Runtime = 'win-x64'; Platform = 'x64'; BuildSegment = 'x64' },
    [pscustomobject]@{ Name = 'x86'; Runtime = 'win-x86'; Platform = 'x86'; BuildSegment = 'x86' },
    [pscustomobject]@{ Name = 'ARM64'; Runtime = 'win-arm64'; Platform = 'ARM64'; BuildSegment = 'ARM64' }
)

Push-Location $repositoryRoot
try {
    if (Test-Path -LiteralPath $artifactRoot) {
        $resolvedArtifactRoot = (Resolve-Path -LiteralPath $artifactRoot).Path
        $resolvedRepositoryRoot = (Resolve-Path -LiteralPath $repositoryRoot).Path
        if (-not $resolvedArtifactRoot.StartsWith($resolvedRepositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clear release output outside this repository: $resolvedArtifactRoot"
        }
        Remove-Item -LiteralPath $resolvedArtifactRoot -Recurse -Force
    }
    $packageDirectory = Join-Path $artifactRoot 'packages'
    $inspectionDirectory = Join-Path $artifactRoot 'inspection'
    New-Item -ItemType Directory -Path $packageDirectory, $inspectionDirectory -Force | Out-Null

    Invoke-External 'dotnet' @('restore', $solution)
    foreach ($architecture in $architectures) {
        Invoke-External 'dotnet' @('clean', $project, '--configuration', 'Release', "-p:Platform=$($architecture.Platform)", '--runtime', $architecture.Runtime)
        Invoke-External 'dotnet' @('build', $project, '--configuration', 'Release', "-p:Platform=$($architecture.Platform)", '--runtime', $architecture.Runtime, '--no-restore')
        $layout = Join-Path $repositoryRoot "src\DuplicateFileCleanerPro.App\bin\$($architecture.BuildSegment)\Release\net10.0-windows10.0.26100.0\$($architecture.Runtime)"
        if (-not (Test-Path -LiteralPath (Join-Path $layout 'AppxManifest.xml'))) {
            throw "The $($architecture.Name) package layout was not produced at $layout."
        }
        $published = Join-Path $artifactRoot (Join-Path 'published' $architecture.Name)
        New-Item -ItemType Directory -Path $published -Force | Out-Null
        Invoke-External 'dotnet' @('publish', $project, '--configuration', 'Release', "-p:Platform=$($architecture.Platform)", '--runtime', $architecture.Runtime, '--self-contained', 'true', '--no-restore', '--output', $published)
        $staging = Join-Path $artifactRoot (Join-Path 'staging' $architecture.Name)
        New-Item -ItemType Directory -Path $staging -Force | Out-Null
        Get-ChildItem -LiteralPath $layout -Force | Copy-Item -Destination $staging -Recurse -Force
        $nestedAppX = Join-Path $staging 'AppX'
        if (Test-Path -LiteralPath $nestedAppX) { Remove-Item -LiteralPath $nestedAppX -Recurse -Force }
        Get-ChildItem -LiteralPath $published -Force | Copy-Item -Destination $staging -Recurse -Force
        Get-ChildItem -LiteralPath $staging -Recurse -File -Filter '*.pdb' | Remove-Item -Force
        Get-ChildItem -LiteralPath $staging -Recurse -File -Filter '*.appxrecipe' | Remove-Item -Force
        $package = Join-Path $packageDirectory "DuplicateFileCleanerPro_$version`_$($architecture.Name).msix"
        Invoke-External $makeAppx @('pack', '/d', $staging, '/p', $package, '/o')
        Assert-PackageLayout -PackagePath $package -ExpectedArchitecture $architecture.Name -InspectionRoot $inspectionDirectory -MakeAppx $makeAppx
    }

    $bundle = Join-Path $artifactRoot "DuplicateFileCleanerPro_$version.msixbundle"
    Invoke-External $makeAppx @('bundle', '/d', $packageDirectory, '/p', $bundle, '/o')
    $uploadZip = Join-Path $artifactRoot "DuplicateFileCleanerPro_$version.zip"
    Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $packageDirectory -Filter '*.msix' | Select-Object -ExpandProperty FullName) -DestinationPath $uploadZip -Force
    $upload = Join-Path $artifactRoot "DuplicateFileCleanerPro_$version.msixupload"
    Move-Item -LiteralPath $uploadZip -Destination $upload -Force

    & "$PSScriptRoot\verify.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'scripts/verify.ps1 failed.' }
    & "$PSScriptRoot\stress.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'scripts/stress.ps1 failed.' }
    & "$PSScriptRoot\e2e.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'scripts/e2e.ps1 failed.' }

    $wackStatus = 'Not run by this invocation. Run from an elevated interactive session with -RunWack.'
    if ($RunWack) {
        if (-not (Test-Path -LiteralPath $appCert)) { throw "Windows App Certification Kit was not found at $appCert." }
        $wackDirectory = Join-Path $artifactRoot 'wack'
        New-Item -ItemType Directory -Path $wackDirectory -Force | Out-Null
        Invoke-External $appCert @('reset')
        Invoke-External $appCert @('test', '-appxpackagepath', $bundle, '-reportoutputpath', (Join-Path $wackDirectory 'WACK.xml'))
        $wackStatus = 'Invoked; inspect the WACK report under artifacts/store/wack.'
    }

    $commit = (& git rev-parse HEAD).Trim()
    $hashRows = Get-ChildItem -LiteralPath $artifactRoot -File | Where-Object { $_.Extension -in '.msix', '.msixbundle', '.msixupload' } | Sort-Object Name | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "| $($_.Name) | $($_.Length) | $hash |"
    }
    @(
        '# Release manifest',
        '',
        "- Product: Duplicate File Cleaner Pro",
        "- Version: $version",
        "- Git commit: $commit",
        '- Architectures: x64, x86, ARM64',
        '- Windows App SDK: 2.3.1',
        '- Minimum OS: Windows 10 build 19041',
        '- Target OS: Windows 11 build 26100',
        '- Verification: verify.ps1, stress.ps1, and e2e.ps1 completed in this invocation.',
        "- WACK: $wackStatus",
        '',
        '| Artifact | Bytes | SHA-256 |',
        '| --- | ---: | --- |'
    ) + $hashRows | Set-Content -LiteralPath (Join-Path $artifactRoot 'RELEASE-MANIFEST.md') -Encoding utf8

    Write-Host "Release artifacts: $artifactRoot"
}
finally {
    Pop-Location
}
