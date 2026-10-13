<#
.SYNOPSIS
    Installs the BYOA agent wallet 0.1 for the current Windows user, from the source code or from the ready package.

.DESCRIPTION
    The same script serves two routes:
      - Source: run from the repository (wallet\install.ps1). It builds the app with the .NET 10 SDK.
      - Package: run from the unzipped download byoa-wallet-<version>-win-x64 (Install.cmd starts it). The package
        already holds the built, self-contained app in app\ and the extension in extension\: nothing is built, and
        no .NET is needed. This mode is chosen by itself when app\Byoa.Wallet.exe lies next to this script, or with
        -FromPackage.

    1. Source: publishes the tray app (Byoa.Wallet.exe), the native messaging host (byoa-host.exe), and the MCP bridge
       (byoa-mcp.exe) into
       -InstallDir. Package: copies app\ into -InstallDir, the extension into -InstallDir\extension and the
       uninstaller (uninstall.ps1, Uninstall.cmd) next to the app, so the unzipped folder is not needed afterwards.
       The copies lose the "downloaded from the internet" mark (Unblock-File), so that Windows does not ask again
       each time a site or the browser starts the wallet.
    2. Writes the native host manifest (org.bringyourownagent.wallet.json) next to them, allowing only this
       repository's extension (its ID is fixed by the "key" in extension\manifest.json).
    3. Registers the host for the current user only, under
         HKCU\Software\Google\Chrome\NativeMessagingHosts\org.bringyourownagent.wallet
         HKCU\Software\Chromium\NativeMessagingHosts\org.bringyourownagent.wallet
       (the second one serves Chromium builds such as Playwright's).
    4. Registers the bringyourownagent:// links (the direct transport, no extension needed) for the current user:
         HKCU\Software\Classes\bringyourownagent   ("URL Protocol"; shell\open\command = "<wallet exe>" --url "%1")
       Nothing else is written to the registry.
    5. Optionally (-StartWithWindows, off by default) puts a shortcut into the user's Startup folder.
    6. Prints the folder to load as an unpacked extension (wallet\extension from the source, or
       -InstallDir\extension from the package).

    Run it again to update; quit the wallet from its tray icon first. uninstall.ps1 (also in the package) removes
    the registry keys and the shortcut again.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File wallet\install.ps1
    From the source: builds and installs.

.EXAMPLE
    Install.cmd
    From the unzipped package (or: powershell -ExecutionPolicy Bypass -File install.ps1 -StartWithWindows).
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'BYOA\wallet-app'),
    [switch]$StartWithWindows,
    [switch]$NoBuild,
    [switch]$FromPackage
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repo = Split-Path -Parent $root
$packageApp = Join-Path $root 'app'
if (-not $FromPackage -and (Test-Path (Join-Path $packageApp 'Byoa.Wallet.exe'))) { $FromPackage = $true }
if ($FromPackage -and -not (Test-Path (Join-Path $packageApp 'Byoa.Wallet.exe'))) {
    throw "No app\Byoa.Wallet.exe next to this script: -FromPackage needs the unzipped package folder (unzip the whole zip first)."
}
$hostName = 'org.bringyourownagent.wallet'
$registryKeys = @(
    "Software\Google\Chrome\NativeMessagingHosts\$hostName",
    "Software\Chromium\NativeMessagingHosts\$hostName"
)
$protocolKey = 'Software\Classes\bringyourownagent'

function Get-ExtensionId([string]$manifestPath) {
    # Chrome derives the ID from the public key: the first 32 hex digits of its SHA-256, written with a-p.
    $key = (Get-Content -Raw -Path $manifestPath | ConvertFrom-Json).key
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $hex = -join ($sha.ComputeHash([Convert]::FromBase64String($key)) | ForEach-Object { $_.ToString('x2') })
    return -join ($hex.Substring(0, 32).ToCharArray() | ForEach-Object { [char]([int][char]'a' + [Convert]::ToInt32([string]$_, 16)) })
}

# The wallet and the host must not be running from the install folder, or their files are locked.
$busy = Get-Process -Name 'Byoa.Wallet', 'byoa-host' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) }
if ($busy) {
    throw "The wallet is running from $InstallDir. Quit it from its tray icon (Quit) and close Chrome's pages that use it, then run install.ps1 again."
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'

# Copies every file of a folder into another one, keeping the subfolders and replacing older copies (an update).
# Files are copied one by one, so an existing target folder is merged into, never nested into.
function Copy-Folder([string]$from, [string]$to) {
    $base = (Resolve-Path -LiteralPath $from).Path.TrimEnd('\')
    $count = 0
    foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File -Force) {
        $target = Join-Path $to $file.FullName.Substring($base.Length + 1)
        $folder = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        # A downloaded package carries the "from the internet" mark on every file, and the copy keeps it. The visitor
        # has already chosen to run this installer, so its own installed copy is unmarked: otherwise Windows could ask
        # again whenever a site or the browser starts the wallet. Only the files copied here are touched.
        Unblock-File -LiteralPath $target
        $count++
    }
    return $count
}

function Copy-FileIfPresent([string]$from, [string]$to) {
    if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { return 0 }
    $folder = Split-Path -Parent $to
    if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
    Copy-Item -LiteralPath $from -Destination $to -Force
    Unblock-File -LiteralPath $to
    return 1
}

if ($FromPackage) {
    # The ready package: the app is already built (self-contained, no .NET needed), so it is only copied. The
    # extension goes next to it, so that Chrome keeps finding it after the unzipped folder is gone.
    Write-Host "Copying the wallet and the native host into $InstallDir ..."
    $copied = Copy-Folder $packageApp $InstallDir
    $extensionDir = Join-Path $InstallDir 'extension'
    $copied += Copy-Folder (Join-Path $root 'extension') $extensionDir
    # The uninstaller goes along too, so the wallet can be removed without the package.
    foreach ($name in 'uninstall.ps1', 'Uninstall.cmd') {
        $file = Join-Path $root $name
        if (Test-Path -LiteralPath $file) {
            Copy-Item -LiteralPath $file -Destination (Join-Path $InstallDir $name) -Force
            Unblock-File -LiteralPath (Join-Path $InstallDir $name)
            $copied++
        }
    }
    # Keep the package's license files with the installed app. The uninstaller deliberately leaves them, like all
    # other visitor files.
    $copied += Copy-FileIfPresent (Join-Path $root 'LICENSE') (Join-Path $InstallDir 'LICENSE')
    $copied += Copy-FileIfPresent (Join-Path $root 'THIRD-PARTY-NOTICES.txt') (Join-Path $InstallDir 'THIRD-PARTY-NOTICES.txt')
    $copied += Copy-FileIfPresent (Join-Path $root 'NOTICE') (Join-Path $InstallDir 'NOTICE')
    $packageLicenses = Join-Path $root 'licenses'
    if (Test-Path -LiteralPath $packageLicenses -PathType Container) {
        $copied += Copy-Folder $packageLicenses (Join-Path $InstallDir 'licenses')
    }
    Write-Host "Copied $copied files."
} else {
    if (-not $NoBuild) {
        Write-Host "Publishing the wallet and the native host into $InstallDir ..."
        & dotnet publish (Join-Path $root 'desktop\Byoa.Wallet.Desktop') -c Release -o $InstallDir --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed for the wallet' }
        & dotnet publish (Join-Path $root 'host\Byoa.Wallet.Host') -c Release -o $InstallDir --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed for the native host' }
        & dotnet publish (Join-Path $root 'mcp\Byoa.Wallet.Mcp') -c Release -o $InstallDir --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed for the MCP bridge' }
    }
    $extensionDir = Join-Path $root 'extension'
    $copied = 0
    # A source checkout has the repository license at its root and the third-party notice beside the packaging
    # scripts. Copy only files that exist; source installs do not require a generated licenses folder.
    $copied += Copy-FileIfPresent (Join-Path $repo 'LICENSE') (Join-Path $InstallDir 'LICENSE')
    $copied += Copy-FileIfPresent (Join-Path $root 'packaging\THIRD-PARTY-NOTICES.txt') (Join-Path $InstallDir 'THIRD-PARTY-NOTICES.txt')
    $copied += Copy-FileIfPresent (Join-Path $repo 'NOTICE') (Join-Path $InstallDir 'NOTICE')
    Write-Host "Copied $copied license files."
}

$hostExe = Join-Path $InstallDir 'byoa-host.exe'
$walletExe = Join-Path $InstallDir 'Byoa.Wallet.exe'
$mcpExe = Join-Path $InstallDir 'byoa-mcp.exe'
if (-not (Test-Path $hostExe) -or -not (Test-Path $walletExe) -or -not (Test-Path $mcpExe)) { throw "byoa-host.exe, byoa-mcp.exe or Byoa.Wallet.exe is missing in $InstallDir" }

$extensionId = Get-ExtensionId (Join-Path $extensionDir 'manifest.json')

# The native host manifest (UTF-8 without a BOM).
$manifestPath = Join-Path $InstallDir "$hostName.json"
$manifest = [ordered]@{
    name            = $hostName
    description     = 'BYOA agent wallet: lets pages use your own AI within your limits'
    path            = $hostExe
    type            = 'stdio'
    allowed_origins = @("chrome-extension://$extensionId/")
}
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))

# Register for the current user: open or create each key (never deleting anything), set its default value.
foreach ($sub in $registryKeys) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($sub)
    try { $key.SetValue('', $manifestPath) } finally { $key.Close() }
    Write-Host "Registered HKCU\$sub"
}

# The bringyourownagent:// scheme (never byoa://, see CONTRACT-DIRECT.md section 6). A link starts the wallet, or
# hands the link to the wallet that is already running.
$key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($protocolKey)
try {
    $key.SetValue('', 'URL:Bring Your Own Agent link')
    $key.SetValue('URL Protocol', '')
} finally { $key.Close() }
$key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$protocolKey\DefaultIcon")
try { $key.SetValue('', "`"$walletExe`",0") } finally { $key.Close() }
$key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$protocolKey\shell\open\command")
try { $key.SetValue('', "`"$walletExe`" --url `"%1`"") } finally { $key.Close() }
Write-Host "Registered HKCU\$protocolKey (bringyourownagent:// links)"

if ($StartWithWindows) {
    $startup = [Environment]::GetFolderPath('Startup')
    $link = Join-Path $startup 'BYOA agent wallet.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $walletExe
    $shortcut.Arguments = '--background'
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Description = 'BYOA agent wallet (tray)'
    $shortcut.Save()
    Write-Host "Start with Windows: $link"
}

Write-Host ''
Write-Host 'Installed the BYOA agent wallet 0.1.'
Write-Host "  App, host, MCP: $InstallDir"
Write-Host "  Data:           $(Join-Path $env:LOCALAPPDATA 'BYOA\wallet') (settings, DPAPI-protected keys, activity)"
Write-Host "  Models:         $(Join-Path $env:LOCALAPPDATA 'BYOA\wallet\models') (models that an older version downloaded to $(Join-Path $env:LOCALAPPDATA 'BYOA\models') stay there and are used from there)"
Write-Host "  Extension ID:   $extensionId"
Write-Host ''
Write-Host 'Load the extension in Chrome: open chrome://extensions, turn on Developer mode,'
Write-Host 'press "Load unpacked" and choose this folder:'
Write-Host "  $extensionDir"
Write-Host 'The extension is optional: a site can also start the wallet with a bringyourownagent:// link, which needs none.'
if ($FromPackage) {
    Write-Host ''
    Write-Host 'The unzipped package folder is not needed any more.'
    Write-Host "To remove the wallet later: $(Join-Path $InstallDir 'Uninstall.cmd')"
    Write-Host '(it removes the registry keys and the Startup shortcut, and leaves your files for you to delete).'
}
