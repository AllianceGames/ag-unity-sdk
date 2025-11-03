cd /d "%~dp0"

@echo off
mkdir Server

mklink /D "Server\Assets" "..\AllianceGamesSdk\Assets"
mklink /D "Server\Packages" "..\AllianceGamesSdk\Packages"
mklink /D "Server\ProjectSettings" "..\AllianceGamesSdk\ProjectSettings"
mklink /D "Server\UserSettings" "..\AllianceGamesSdk\UserSettings"

echo Done.
pause
