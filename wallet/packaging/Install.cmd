@echo off
rem BYOA agent wallet: installs this unzipped package for the current Windows user (see README.txt).
rem It runs install.ps1 from this folder; arguments are passed on (for example: Install.cmd -StartWithWindows).
setlocal
if not exist "%~dp0install.ps1" goto :notunzipped
if not exist "%~dp0app\Byoa.Wallet.exe" goto :notunzipped
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
set "rc=%errorlevel%"
echo.
pause
exit /b %rc%

:notunzipped
echo Unzip the whole package first (right click the zip file, Extract All...),
echo then run Install.cmd from the unzipped folder.
echo.
pause
exit /b 1
