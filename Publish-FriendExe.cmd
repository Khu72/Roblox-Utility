@echo off
setlocal
pushd "%~dp0"
dotnet msbuild "RobloxUtility\RobloxUtility.csproj" -t:PublishFriendExe
set ERR=%ERRORLEVEL%
popd
if %ERR% neq 0 exit /b %ERR%
echo.
echo Single EXE: RobloxUtility\bin\Release\net8.0-windows\win-x64\publish\RobloxUtility.exe
exit /b 0
