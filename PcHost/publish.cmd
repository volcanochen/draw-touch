@echo off
set "DN="
where dotnet >nul 2>&1 && set "DN=dotnet"
if not defined DN if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "DN=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not defined DN if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DN=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DN if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DN=%USERPROFILE%\.dotnet\dotnet.exe"
if not defined DN (
  echo 未找到 dotnet，请先安装 .NET 8 SDK: https://dotnet.microsoft.com/download
  pause
  exit /b 1
)
"%DN%" publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
echo.
echo 发布完成：SpenTablet\PcHost\publish\SpenTabletHost.exe
pause
