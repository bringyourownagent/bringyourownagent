<#
.SYNOPSIS
    Builds and installs the BYOA agent wallet 0.1 for the current Windows user.

.DESCRIPTION
    1. Publishes the tray app (Byoa.Wallet.exe) and the native messaging host (byoa-host.exe) into -InstallDir.
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
    6. Prints the folder to load as an unpacked extension.

    Needs the .NET 10 SDK. Run it again to update; quit the wallet from its tray icon first.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File wallet\install.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'BYOA\wallet-app'),
    [switch]$StartWithWindows,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
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

if (-not $NoBuild) {
    Write-Host "Publishing the wallet and the native host into $InstallDir ..."
    & dotnet publish (Join-Path $root 'desktop\Byoa.Wallet.Desktop') -c Release -o $InstallDir --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed for the wallet' }
    & dotnet publish (Join-Path $root 'host\Byoa.Wallet.Host') -c Release -o $InstallDir --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed for the native host' }
}

$hostExe = Join-Path $InstallDir 'byoa-host.exe'
$walletExe = Join-Path $InstallDir 'Byoa.Wallet.exe'
if (-not (Test-Path $hostExe) -or -not (Test-Path $walletExe)) { throw "byoa-host.exe or Byoa.Wallet.exe is missing in $InstallDir" }

$extensionDir = Join-Path $root 'extension'
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
Write-Host "  App and host:   $InstallDir"
Write-Host "  Data:           $(Join-Path $env:LOCALAPPDATA 'BYOA\wallet') (settings, DPAPI-protected keys, activity)"
Write-Host "  Models:         $(Join-Path $env:LOCALAPPDATA 'BYOA\models')"
Write-Host "  Extension ID:   $extensionId"
Write-Host ''
Write-Host 'Load the extension in Chrome: open chrome://extensions, turn on Developer mode,'
Write-Host 'press "Load unpacked" and choose this folder:'
Write-Host "  $extensionDir"
