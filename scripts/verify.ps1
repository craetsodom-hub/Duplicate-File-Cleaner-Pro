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

function Assert-NoRipgrepMatch {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string[]]$Paths
    )

    $matches = & rg --line-number --glob '*.cs' --glob '*.xaml' --glob '*.csproj' $Pattern @Paths
    $result = $LASTEXITCODE
    if ($result -eq 0) {
        throw "$Description check failed:`n$($matches -join [Environment]::NewLine)"
    }

    if ($result -ne 1) {
        throw "$Description check could not run (rg exit code $result)."
    }

    Write-Host "> ${Description}: clean"
}

function Assert-ReferenceHashes {
    $expected = [ordered]@{
        '01-home-scan-setup.png' = 'EF15BA237C48EC941DA9533DA0E60B3239FB0AA0FB68BD4040A77C93E8AF247E'
        '02-scanning.png' = 'DA21C628C0304ACDECBCC5B0108B29C0A72728B792031243CC5C5405BC2119EB'
        '03-results.png' = '934B1A869F01CF6D6280CA209D03074CB62E46AFE4DFA799D6565429B213D95F'
        '04-cleanup.png' = 'A42D38A709D9A7E531BE224067FFE9B535C7038035B3FDF09D8D1A6F297FF367'
        '05-settings.png' = 'E521BD36B43F596459677FFED5DA5C518A0927B4A1624CFF44E3358D4535B79D'
    }

    foreach ($entry in $expected.GetEnumerator()) {
        $path = Join-Path 'docs\design-references' $entry.Key
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($actual -ne $entry.Value) {
            throw "Reference hash mismatch for $path. Expected $($entry.Value), found $actual."
        }
    }

    Write-Host '> visual reference SHA-256 integrity: verified'
}

function Assert-RecycleBinBoundary {
    $boundary = 'src\DuplicateFileCleanerPro.Infrastructure.Windows\Cleanup\WindowsShellRecycleBin.cs'
    $required = @(
        'FileOperationRecycleOnDelete = 0x00080000',
        'FileOperationAddUndoRecord = 0x20000000',
        'operation.DeleteItem',
        'operation.PerformOperations'
    )

    $text = Get-Content -LiteralPath $boundary -Raw
    foreach ($fragment in $required) {
        if ($text.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
            throw "Recycle Bin boundary is missing required structure: $fragment"
        }
    }

    $unexpected = & rg --line-number --glob '*.cs' --glob '!WindowsShellRecycleBin.cs' 'DeleteItem\s*\(|PerformOperations\s*\(' 'src'
    $result = $LASTEXITCODE
    if ($result -eq 0) {
        throw "Destructive Shell operation escaped the audited boundary:`n$($unexpected -join [Environment]::NewLine)"
    }
    if ($result -ne 1) {
        throw "Recycle Bin boundary search failed with rg exit code $result."
    }

    Write-Host '> Recycle Bin-only boundary: verified'
}

function Assert-AccessibilityMarkers {
    $xaml = Get-Content -LiteralPath 'src\DuplicateFileCleanerPro.App\MainWindow.xaml' -Raw
    $codeBehind = Get-Content -LiteralPath 'src\DuplicateFileCleanerPro.App\MainWindow.xaml.cs' -Raw
    $resources = Get-Content -LiteralPath 'src\DuplicateFileCleanerPro.App\Strings\en-US\Resources.resw' -Raw
    foreach ($marker in @('ResultsSelectionNotice', 'AutomationProperties.LiveSetting="Polite"', 'AutomationProperties.HeadingLevel="Level1"')) {
        if ($xaml.IndexOf($marker, [StringComparison]::Ordinal) -lt 0) {
            throw "Accessibility marker is missing from the shell: $marker"
        }
    }
    if ($codeBehind.IndexOf('AccessibilitySettings().HighContrast', [StringComparison]::Ordinal) -lt 0) {
        throw 'High-contrast caption-color fallback is missing.'
    }
    if ($codeBehind.IndexOf('AutomationProperties.SetLiveSetting(CleanupActivityText', [StringComparison]::Ordinal) -lt 0) {
        throw 'Cleanup progress live-status marker is missing.'
    }
    foreach ($key in @('ResultsSelectionNotice.Message', 'ResultsDescendingButton.AutomationProperties.Name', 'ScanProgressBar.AutomationProperties.Name')) {
        $expectedName = 'name="' + $key + '"'
        if ($resources.IndexOf($expectedName, [StringComparison]::Ordinal) -lt 0) {
            throw "Localized accessibility resource is missing: $key"
        }
    }

    Write-Host '> accessibility shell markers: verified'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    Invoke-DotNet @('restore', 'DuplicateFileCleanerPro.sln', '--runtime', 'win-x64')
    Invoke-DotNet @('build', 'DuplicateFileCleanerPro.sln', '--configuration', 'Release', '-p:Platform=x64', '--no-restore')
    Invoke-DotNet @('test', 'DuplicateFileCleanerPro.sln', '--configuration', 'Release', '-p:Platform=x64', '--no-build', '--filter', 'TestCategory!=Stress')
    Assert-NoRipgrepMatch -Description 'production forbidden mutation API' -Pattern '\b(File|Directory)\.(Delete|Move|Replace|Write(AllBytes|AllText)?|Append(AllText)?|Create(SymbolicLink|HardLink)?)\b|\bDeleteFile(W|A)?\b|FileMode\.(Create|CreateNew|Append|Truncate)|SetAccessControl\s*\(|SetOwner\s*\(|SHFileOperation|FOF_WANTNUKEWARNING' -Paths @('src')
    Assert-RecycleBinBoundary
    Assert-NoRipgrepMatch -Description 'synchronous async blocking' -Pattern '\.Result\b|\.Wait\s*\(|GetAwaiter\s*\(\s*\)\s*\.GetResult\s*\(' -Paths @('src')
    Assert-NoRipgrepMatch -Description 'QA hook leakage' -Pattern 'TemporaryQa|Phase4\.QA|Phase7\.QA|QA-root|automatic root selection' -Paths @('src')
    Assert-NoRipgrepMatch -Description 'network, telemetry, and upload API' -Pattern 'HttpClient|WebRequest|Socket|Telemetry|Analytics|Upload|ApplicationInsights|Sentry|Microsoft\.Data\.Sqlite|EntityFramework|LiteDB' -Paths @('src')
    Assert-AccessibilityMarkers
    Assert-ReferenceHashes
    & git diff --check
    if ($LASTEXITCODE -ne 0) {
        throw 'git diff --check failed.'
    }
}
finally {
    Pop-Location
}
