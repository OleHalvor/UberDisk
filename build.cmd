@echo off
rem Builds UberDisk (Release) into .\publish and starts it.
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul || (echo The .NET SDK is not installed. Get it from https://dot.net & exit /b 1)
dotnet publish src\UberDisk.App\UberDisk.App.csproj -c Release -o publish || exit /b 1
echo.
echo Built: %CD%\publish\UberDisk.exe
if /i not "%1"=="--no-run" start "" "%CD%\publish\UberDisk.exe"
