<#
.SYNOPSIS
    Builds the ready-to-install package of the BYOA agent wallet for Windows: byoa-wallet-<version>-win-x64.zip.

.DESCRIPTION
    1. Publishes the tray app (desktop\Byoa.Wallet.Desktop), the native messaging host (host\Byoa.Wallet.Host), and
       the MCP bridge (mcp\Byoa.Wallet.Mcp), Release, self-contained for win-x64 (the .NET runtime is included, so
       the visitor's PC needs no .NET),
       not trimmed and not single-file, into one folder. The build happens under -OutDir (artifacts\), never in
       the repository's own bin\ and obj\ folders.
    2. Stages the package folder byoa-wallet-<version>-win-x64\:
         app\                  Byoa.Wallet.exe, byoa-host.exe, byoa-mcp.exe and the runtime (native libraries for win-x64 only)
         extension\            the unpacked Chrome extension (wallet\extension)
         install.ps1           wallet\install.ps1 itself: it notices app\ next to it and installs the package
         uninstall.ps1         wallet\uninstall.ps1 itself
         Install.cmd           double-click helpers for the two scripts
         Uninstall.cmd
         README.txt            what it is, how to install, SmartScreen, where data lives, how to remove it
         LICENSE               Apache License 2.0
         THIRD-PARTY-NOTICES.txt
         licenses\             the license and notice files of the packages inside app\, and of the fonts
    3. Zips it (the folder is the zip's single top folder, entries with forward slashes) into
       <OutDir>\byoa-wallet-<version>-win-x64.zip, writes <zip>.sha256 ("<sha256>  <file name>") next to it and
       prints the size and the SHA-256.

    Nothing is overwritten: -OutDir must not hold an earlier package of the same version. Needs the .NET 10 SDK.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File wallet\packaging\make-package.ps1
    Builds into wallet\.publish\package-<date-time>\ (ignored by git).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File wallet\packaging\make-package.ps1 -OutDir D:\builds\byoa
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$packaging = $PSScriptRoot
$wallet = Split-Path -Parent $packaging
$repo = Split-Path -Parent $wallet
$rid = 'win-x64'

if (-not $Version) {
    # The version every project of the wallet is built with (Directory.Build.props).
    [xml]$props = Get-Content -Raw -Path (Join-Path $wallet 'Directory.Build.props')
    $Version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
    if (-not $Version) { throw 'No <Version> in wallet\Directory.Build.props; pass -Version.' }
}
if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') { throw "Not a version: $Version" }

if (-not $OutDir) { $OutDir = Join-Path $wallet ('.publish\package-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$name = "byoa-wallet-$Version-$rid"
$stage = Join-Path $OutDir $name
$zip = Join-Path $OutDir "$name.zip"
$publish = Join-Path $OutDir 'publish'
$artifacts = Join-Path $OutDir 'artifacts'
foreach ($path in $stage, $zip, "$zip.sha256", $publish) {
    if (Test-Path -LiteralPath $path) { throw "$path already exists. Choose another -OutDir: this script overwrites nothing." }
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'

# 1. Publish the wallet, host and MCP bridge into one folder. They share the same runtime and core library.
$app = Join-Path $publish 'app'
foreach ($project in 'desktop\Byoa.Wallet.Desktop\Byoa.Wallet.Desktop.csproj', 'host\Byoa.Wallet.Host\Byoa.Wallet.Host.csproj', 'mcp\Byoa.Wallet.Mcp\Byoa.Wallet.Mcp.csproj') {
    Write-Host "Publishing $project (Release, self-contained, $rid) ..."
    & dotnet publish (Join-Path $wallet $project) -c Release -r $rid --self-contained true `
        -p:PublishTrimmed=false -p:PublishSingleFile=false -p:Version=$Version `
        --artifacts-path $artifacts -o $app --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}
foreach ($file in 'Byoa.Wallet.exe', 'byoa-host.exe', 'byoa-mcp.exe', 'hostfxr.dll', 'coreclr.dll', 'Byoa.Wallet.runtimeconfig.json', 'byoa-host.runtimeconfig.json', 'byoa-mcp.runtimeconfig.json') {
    if (-not (Test-Path -LiteralPath (Join-Path $app $file))) { throw "$file is missing in $app" }
}
foreach ($config in 'Byoa.Wallet.runtimeconfig.json', 'byoa-host.runtimeconfig.json', 'byoa-mcp.runtimeconfig.json') {
    # A self-contained app lists the frameworks it carries ("includedFrameworks") instead of needing one ("frameworks").
    $json = Get-Content -Raw -Path (Join-Path $app $config) | ConvertFrom-Json
    if (-not $json.runtimeOptions.includedFrameworks -or $json.runtimeOptions.frameworks) { throw "$config is not self-contained" }
}

# 2. Stage the package. Native libraries that Whisper.net ships for other systems (runtimes\linux-*, osx, arm, x86)
#    are left out; the wallet runs on Windows x64 only.
function Test-Wanted([string]$relative) {
    $parts = $relative.Split('\')
    if ($relative -eq 'ggml-metal.metal') { return $false }          # a shader for Apple's Metal
    if ($parts[0] -ne 'runtimes' -or $parts.Count -lt 3) { return $true }
    if ($parts[1] -eq $rid) { return $true }
    return ($parts[1] -eq 'vulkan' -and $parts.Count -ge 4 -and $parts[2] -eq $rid)
}
function Copy-Into([string]$from, [string]$to, [switch]$Filter) {
    $base = (Resolve-Path -LiteralPath $from).Path.TrimEnd('\')
    $count = 0
    foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File -Force) {
        $relative = $file.FullName.Substring($base.Length + 1)
        if ($Filter -and -not (Test-Wanted $relative)) { continue }
        $target = Join-Path $to $relative
        $folder = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
        Copy-Item -LiteralPath $file.FullName -Destination $target
        $count++
    }
    return $count
}
# Text files of the package get Windows line ends; .cmd files must be plain ASCII (cmd.exe does not read a BOM).
function Write-Text([string]$from, [string]$to, [switch]$Ascii) {
    $text = [System.IO.File]::ReadAllText($from).Replace("`r`n", "`n").Replace("`n", "`r`n").Replace('{VERSION}', $Version)
    if ($text -match '[^\x00-\x7F]') { throw "$from has characters outside ASCII" }
    $encoding = if ($Ascii) { New-Object System.Text.ASCIIEncoding } else { New-Object System.Text.UTF8Encoding($false) }
    [System.IO.File]::WriteAllText($to, $text, $encoding)
}

Write-Host "Staging $stage ..."
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$appFiles = Copy-Into $app (Join-Path $stage 'app') -Filter
$extensionFiles = Copy-Into (Join-Path $wallet 'extension') (Join-Path $stage 'extension')
foreach ($script in 'install.ps1', 'uninstall.ps1') {
    # The scripts run under Windows PowerShell 5.1, which reads a file without a BOM as ANSI: they must stay ASCII.
    Write-Text (Join-Path $wallet $script) (Join-Path $stage $script)
}
Write-Text (Join-Path $packaging 'Install.cmd') (Join-Path $stage 'Install.cmd') -Ascii
Write-Text (Join-Path $packaging 'Uninstall.cmd') (Join-Path $stage 'Uninstall.cmd') -Ascii
Write-Text (Join-Path $packaging 'README.txt') (Join-Path $stage 'README.txt')
Write-Text (Join-Path $packaging 'THIRD-PARTY-NOTICES.txt') (Join-Path $stage 'THIRD-PARTY-NOTICES.txt')
Write-Text (Join-Path $repo 'LICENSE') (Join-Path $stage 'LICENSE')

# The license and notice files of every package the two programs carry (their .deps.json files list them, the
# runtime included), copied as they are from the NuGet packages folder the build used, plus the fonts' license.
$packages = $env:NUGET_PACKAGES
if (-not $packages) {
    $line = & dotnet nuget locals global-packages --list | Select-Object -First 1
    if ($line -match ':\s*(.+)$') { $packages = $Matches[1].Trim() }
}
if (-not $packages -or -not (Test-Path -LiteralPath $packages)) { throw 'The NuGet packages folder was not found (set NUGET_PACKAGES).' }
$licenses = Join-Path $stage 'licenses'
$licenseFiles = 0
$seen = @{}
foreach ($deps in 'Byoa.Wallet.deps.json', 'byoa-host.deps.json', 'byoa-mcp.deps.json') {
    $libraries = (Get-Content -Raw -Path (Join-Path $app $deps) | ConvertFrom-Json).libraries
    foreach ($library in $libraries.PSObject.Properties) {
        if ($library.Value.type -notin 'package', 'runtimepack') { continue }
        $id, $libraryVersion = $library.Name.Split('/')
        if ($library.Value.type -eq 'runtimepack') { $id = $id -replace '^runtimepack\.', '' }
        $folder = Join-Path $packages ($id.ToLowerInvariant() + '\' + $libraryVersion.ToLowerInvariant())
        if ($seen.ContainsKey($folder)) { continue }
        $seen[$folder] = $true
        if (-not (Test-Path -LiteralPath $folder)) { throw "Package folder not found: $folder" }
        foreach ($file in Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name -match '^(LICEN[CS]E|NOTICE|THIRD-PARTY-NOTICES)' }) {
            $target = Join-Path $licenses "$id-$libraryVersion"
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $target $file.Name)
            $licenseFiles++
        }
    }
}
New-Item -ItemType Directory -Force -Path (Join-Path $licenses 'Barlow-fonts') | Out-Null
Copy-Item -LiteralPath (Join-Path $wallet 'desktop\Byoa.Wallet.Desktop\Assets\Fonts\OFL.txt') -Destination (Join-Path $licenses 'Barlow-fonts\OFL.txt')
$licenseFiles++
Write-Host "  app: $appFiles files, extension: $extensionFiles files, licenses: $licenseFiles files"

# 3. Zip it with the package folder as the single top folder; entry names use forward slashes.
Add-Type -AssemblyName System.IO.Compression
Write-Host "Zipping $zip ..."
$stream = [System.IO.File]::Open($zip, [System.IO.FileMode]::CreateNew)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File -Force | Sort-Object FullName) {
            $entryName = $name + '/' + $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
            $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]$file.LastWriteTime
            $in = [System.IO.File]::OpenRead($file.FullName)
            try {
                $out = $entry.Open()
                try { $in.CopyTo($out) } finally { $out.Dispose() }
            } finally { $in.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }

$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText("$zip.sha256", "$hash  $name.zip`n", (New-Object System.Text.ASCIIEncoding))
$size = (Get-Item -LiteralPath $zip).Length
Write-Host ''
Write-Host "Package:  $zip"
Write-Host ("Size:     {0:N1} MB ({1} bytes)" -f ($size / 1MB), $size)
Write-Host "SHA-256:  $hash"
Write-Host "Folder:   $stage"
