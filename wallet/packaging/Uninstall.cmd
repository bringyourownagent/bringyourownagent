@echo off
rem BYOA agent wallet: removes the registry keys and the Startup shortcut that Install.cmd made (see README.txt).
rem It runs uninstall.ps1 from this folder; arguments are passed on (for example: Uninstall.cmd -WhatIf).
rem No files are deleted: the folders the wallet used are printed, for you to remove if you wish.
setlocal
if not exist "%~dp0uninstall.ps1" goto :missing
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
set "rc=%errorlevel%"
echo.
pause
exit /b %rc%

:missing
echo uninstall.ps1 is missing next to Uninstall.cmd. Unzip the whole package first,
echo or run Uninstall.cmd from the folder the wallet was installed into.
echo.
pause
exit /b 1
