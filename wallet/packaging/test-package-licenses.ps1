<#
.SYNOPSIS
    Tests the license check of make-package.ps1 (Copy-PackageLicenses) on small fixture folders. Builds no package.

.DESCRIPTION
    Loads the function from make-package.ps1 with the PowerShell parser, so the packaging script stays the only copy of the
    code. Each case writes fake .deps.json and project.assets.json files and fake NuGet package folders into a new folder
    under -WorkFolder, and deletes that folder at the end. Prints one line per case; exits with an error if a case fails.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File wallet\packaging\test-package-licenses.ps1 -WorkFolder D:\scratch
#>
[CmdletBinding()]
param([string]$WorkFolder = [System.IO.Path]::GetTempPath())

$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'make-package.ps1'
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$errors)
if ($errors) { throw "$script does not parse: $($errors[0].Message)" }
$definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Copy-PackageLicenses' }, $true)
if (-not $definition) { throw 'Copy-PackageLicenses is not defined in make-package.ps1' }
. ([scriptblock]::Create($definition.Extent.Text))

$root = Join-Path $WorkFolder ('license-check-' + [guid]::NewGuid().ToString('N'))
$failed = 0

function Report([string]$case, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host "PASS $case" } else { Write-Host "FAIL ${case}: $detail"; $script:failed++ }
}

# A fixture: an app folder with the three .deps.json files, a folder for the build's project.assets.json files, a fake
# NuGet packages folder and the output folder.
function New-Case([string]$name) {
    $dir = Join-Path $root $name
    $case = @{
        App = Join-Path $dir 'app'; Artifacts = Join-Path $dir 'artifacts'; Packages = Join-Path $dir 'packages'
        To = Join-Path $dir 'licenses'; Deps = @{}; Assets = @{}
    }
    New-Item -ItemType Directory -Force -Path $case.App, $case.Artifacts, $case.Packages | Out-Null
    return $case
}

# Makes the NuGet folder of a package, with a LICENSE file and/or a content folder.
function Add-Package($case, [string]$id, [string]$version, [switch]$License, [switch]$Content) {
    $folder = Join-Path $case.Packages ($id.ToLowerInvariant() + '\' + $version)
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    if ($License) { Set-Content -LiteralPath (Join-Path $folder 'LICENSE') -Value "license of $id $version" -Encoding ASCII }
    if ($Content) { New-Item -ItemType Directory -Force -Path (Join-Path $folder 'content') | Out-Null }
}

function Save-Case($case) {
    foreach ($file in 'Byoa.Wallet.deps.json', 'byoa-host.deps.json', 'byoa-mcp.deps.json') {
        $libraries = if ($file -eq 'Byoa.Wallet.deps.json') { $case.Deps } else { @{} }
        [ordered]@{ libraries = $libraries } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $case.App $file) -Encoding ASCII
    }
    $objFolder = Join-Path $case.Artifacts 'obj\Byoa.Wallet.Desktop'
    New-Item -ItemType Directory -Force -Path $objFolder | Out-Null
    [ordered]@{ libraries = $case.Assets } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $objFolder 'project.assets.json') -Encoding ASCII
}

# Returns the error message of the check, or '' when it passes.
function Invoke-Check($case, [string[]]$noLicenseFile) {
    try {
        Copy-PackageLicenses $case.App $case.Artifacts $case.Packages $case.To $noLicenseFile | Out-Null
        return ''
    } catch {
        return $_.Exception.Message
    }
}

try {
    $c = New-Case 'copies-a-license'
    Add-Package $c 'good.pkg' '1.0.0' -License
    $c.Deps['good.pkg/1.0.0'] = @{ type = 'package' }
    Save-Case $c
    $copied = Copy-PackageLicenses $c.App $c.Artifacts $c.Packages $c.To @()
    Report 'a package with a license file is copied' ((Test-Path -LiteralPath (Join-Path $c.To 'good.pkg-1.0.0\LICENSE')) -and $copied -eq 1) "copied=$copied"

    $c = New-Case 'refuses-unlisted'
    Add-Package $c 'bare.pkg' '1.0.0'
    $c.Deps['bare.pkg/1.0.0'] = @{ type = 'package' }
    Save-Case $c
    $message = Invoke-Check $c @()
    Report 'a package with no license file and not listed fails, and the message names it' ($message -like '*bare.pkg/1.0.0 has no license file*') "message: '$message'"

    $c = New-Case 'accepts-listed'
    Add-Package $c 'listed.pkg' '2.0.0'
    $c.Deps['listed.pkg/2.0.0'] = @{ type = 'package' }
    Save-Case $c
    $message = Invoke-Check $c @('listed.pkg/2.0.0')
    Report 'a package with no license file that is listed passes and copies nothing' (($message -eq '') -and -not (Test-Path -LiteralPath (Join-Path $c.To 'listed.pkg-2.0.0'))) "message: '$message'"

    $c = New-Case 'runtime-pack'
    Add-Package $c 'some.runtime.win-x64' '3.0.0' -License
    $c.Deps['runtimepack.Some.Runtime.win-x64/3.0.0'] = @{ type = 'runtimepack' }
    Save-Case $c
    Copy-PackageLicenses $c.App $c.Artifacts $c.Packages $c.To @() | Out-Null
    Report 'a runtime pack is copied under its own name' (Test-Path -LiteralPath (Join-Path $c.To 'Some.Runtime.win-x64-3.0.0\LICENSE')) 'no copy'

    $c = New-Case 'build-target-package-refused'
    Add-Package $c 'runtime.pkg' '1.9.1' -Content
    $c.Assets['runtime.pkg/1.9.1'] = @{ type = 'package'; path = 'runtime.pkg/1.9.1' }
    Save-Case $c
    $message = Invoke-Check $c @()
    Report 'a package found only in project.assets.json (with a content folder) is checked too' ($message -like '*runtime.pkg/1.9.1 has no license file*') "message: '$message'"

    $c = New-Case 'build-target-package-copied'
    Add-Package $c 'runtime2.pkg' '1.0.0' -License -Content
    $c.Assets['runtime2.pkg/1.0.0'] = @{ type = 'package'; path = 'runtime2.pkg/1.0.0' }
    Save-Case $c
    Copy-PackageLicenses $c.App $c.Artifacts $c.Packages $c.To @() | Out-Null
    Report 'a content package with a license file is copied from project.assets.json' (Test-Path -LiteralPath (Join-Path $c.To 'runtime2.pkg-1.0.0\LICENSE')) 'no copy'

    $c = New-Case 'assets-without-content-ignored'
    Add-Package $c 'build.only' '1.0.0'
    $c.Assets['build.only/1.0.0'] = @{ type = 'package'; path = 'build.only/1.0.0' }
    Save-Case $c
    $message = Invoke-Check $c @()
    Report 'a package in project.assets.json without a content folder is not checked' ($message -eq '') "message: '$message'"

    $c = New-Case 'listed-twice-copied-once'
    Add-Package $c 'both.pkg' '1.0.0' -License -Content
    $c.Deps['both.pkg/1.0.0'] = @{ type = 'package' }
    $c.Assets['both.pkg/1.0.0'] = @{ type = 'package'; path = 'both.pkg/1.0.0' }
    Save-Case $c
    $copied = Copy-PackageLicenses $c.App $c.Artifacts $c.Packages $c.To @()
    Report 'a package in both files is copied once' ($copied -eq 1) "copied=$copied"

    $c = New-Case 'missing-folder'
    $c.Deps['gone.pkg/1.0.0'] = @{ type = 'package' }
    Save-Case $c
    $message = Invoke-Check $c @()
    Report 'a package folder that is not there stops the check' ($message -like '*Package folder not found*gone.pkg*') "message: '$message'"
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}

if ($failed -gt 0) { throw "$failed license check case(s) failed" }
Write-Host 'All license check cases pass.'
