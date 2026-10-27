@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo  The .NET SDK is needed to BUILD the app ^(not to run it^).
  echo  Install ".NET SDK" from: https://dotnet.microsoft.com/download
  echo  Then run this file again.
  echo.
  start "" "https://dotnet.microsoft.com/download"
  pause
  exit /b 1
)

echo Building Messenger Hub...
dotnet restore MessengerHub.csproj --configfile nuget.config
if errorlevel 1 (
  echo.
  echo  Could not download packages from nuget.org.
  echo  Check your internet connection, VPN or firewall, then try again.
  pause
  exit /b 1
)
dotnet build MessengerHub.csproj -c Release --no-restore -o "dist\MessengerHub"
if errorlevel 1 (
  echo.
  echo  Build failed. Copy the error above and send it to Claude.
  pause
  exit /b 1
)

echo.
echo  Done! Your portable app is in:  dist\MessengerHub
echo  Copy that whole folder anywhere and run MessengerHub.exe
echo.
explorer "dist\MessengerHub"
pause
