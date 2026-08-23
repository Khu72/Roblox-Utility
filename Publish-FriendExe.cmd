@echo off
setlocal EnableExtensions
pushd "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
  echo.
  echo Publish-FriendExe needs the .NET 8 SDK, but "dotnet" was not found on PATH.
  echo Install it from: https://dotnet.microsoft.com/download/dotnet/8.0
  echo Then run this script again.
  echo.
  pause
  popd
  exit /b 1
)

dotnet --list-sdks 2>nul | findstr /R "." >nul
if errorlevel 1 (
  echo.
  echo Publish-FriendExe needs the .NET 8 SDK.
  echo This PC only has the .NET runtime, which cannot compile the app.
  echo Install the SDK from: https://dotnet.microsoft.com/download/dotnet/8.0
  echo Then run this script again.
  echo.
  pause
  popd
  exit /b 1
)

dotnet publish "RobloxUtility\RobloxUtility.csproj" ^
  -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=false ^
  -p:PublishTrimmed=false ^
  -p:DebugType=none ^
  -p:DebugSymbols=false
set ERR=%ERRORLEVEL%
popd
if %ERR% neq 0 (
  echo.
  echo Publish failed with exit code %ERR%.
  pause
  exit /b %ERR%
)

echo.
echo Single EXE: RobloxUtility\bin\Release\net8.0-windows\win-x64\publish\RobloxUtility.exe
exit /b 0
