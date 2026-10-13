<#
.SYNOPSIS
    Unregisters the BYOA agent wallet 0.1 for the current Windows user.

.DESCRIPTION
    Removes exactly what install.ps1 created outside the app folder:
      HKCU\Software\Google\Chrome\NativeMessagingHosts\org.bringyourownagent.wallet   (registry key)
      HKCU\Software\Chromium\NativeMessagingHosts\org.bringyourownagent.wallet         (registry key)
      HKCU\Software\Classes\bringyourownagent   (registry key with its DefaultIcon and shell\open\command
                                                 subkeys; only while its command is this wallet's)
      the Startup shortcut "BYOA agent wallet.lnk", only if install.ps1 -StartWithWindows made one (one file)
    Nothing else.

    Apart from that one shortcut it deletes no files: it prints the folders the wallet used (the app, its data with
    your settings and DPAPI-protected keys, downloaded models), so that you can remove them yourself if you want to.
    Remove the unpacked extension in chrome://extensions.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File wallet\uninstall.ps1
    powershell -ExecutionPolicy Bypass -File wallet\uninstall.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'BYOA\wallet-app')
)

$ErrorActionPreference = 'Stop'
$hostName = 'org.bringyourownagent.wallet'
$registryKeys = @(
    "Software\Google\Chrome\NativeMessagingHosts\$hostName",
    "Software\Chromium\NativeMessagingHosts\$hostName"
)
$protocolKey = 'Software\Classes\bringyourownagent'

foreach ($sub in $registryKeys) {
    $existing = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($sub)
    if ($null -eq $existing) {
        Write-Host "Not registered: HKCU\$sub"
        continue
    }
    $existing.Close()
    if ($PSCmdlet.ShouldProcess("HKCU\$sub", 'Remove registry key')) {
        # The key holds only its default value; no subkeys are created by install.ps1.
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($sub, $false)
        Write-Host "Removed HKCU\$sub"
    }
}

# The bringyourownagent:// links: removed only while they still start this wallet (another program may have taken
# the scheme since; its registration is then left alone).
$command = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$protocolKey\shell\open\command")
if ($null -eq $command) {
    Write-Host "Not registered: HKCU\$protocolKey"
} else {
    $value = [string]$command.GetValue('')
    $command.Close()
    if ($value -notmatch 'Byoa\.Wallet\.exe') {
        Write-Host "Left in place: HKCU\$protocolKey now starts another program ($value)"
    } elseif ($PSCmdlet.ShouldProcess("HKCU\$protocolKey", 'Remove registry key and its subkeys')) {
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($protocolKey, $false)
        Write-Host "Removed HKCU\$protocolKey"
    }
}

$link = Join-Path ([Environment]::GetFolderPath('Startup')) 'BYOA agent wallet.lnk'
if (Test-Path $link) {
    if ($PSCmdlet.ShouldProcess($link, 'Remove the Startup shortcut')) {
        Remove-Item -LiteralPath $link
        Write-Host "Removed $link"
    }
}

Write-Host ''
if ($WhatIfPreference) {
    Write-Host 'Dry run: nothing was changed. Without -WhatIf the keys above are removed; files are never deleted:'
} else {
    Write-Host 'The wallet is no longer registered with Chrome or for bringyourownagent:// links. Files were left in place; remove them yourself if you wish:'
}
Write-Host "  App and host:   $InstallDir"
Write-Host "  Data:           $(Join-Path $env:LOCALAPPDATA 'BYOA\wallet')"
Write-Host "  Models:         $(Join-Path $env:LOCALAPPDATA 'BYOA\wallet\models') (and $(Join-Path $env:LOCALAPPDATA 'BYOA\models') if an older version of the wallet downloaded them there)"
Write-Host 'Quit the wallet from its tray icon, and remove the unpacked extension in chrome://extensions.'
