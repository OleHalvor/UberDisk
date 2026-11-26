@echo off
rem Builds UberDisk (Release) into .\publish and starts it.
rem   build.cmd            quick build (needs the .NET 8 desktop runtime to run)
rem   build.cmd release    the GitHub downloads: one self-contained UberDisk.exe (portable) in dist\UberDisk,
rem                        and the installer dist\UberDisk-Setup.exe that puts it in %LOCALAPPDATA%\Programs
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul || (echo The .NET SDK is not installed. Get it from https://dot.net & exit /b 1)
if /i "%1"=="release" goto release
dotnet publish src\UberDisk.App\UberDisk.App.csproj -c Release -o publish || exit /b 1
echo.
echo Built: %CD%\publish\UberDisk.exe
if /i not "%1"=="--no-run" start "" "%CD%\publish\UberDisk.exe"
exit /b 0

:release
dotnet test -c Release || exit /b 1
if exist dist rmdir /s /q dist
dotnet publish src\UberDisk.App\UberDisk.App.csproj -c Release -p:UberDiskRelease=true -o dist\UberDisk || exit /b 1
dotnet build installer\UberDisk.Setup.proj -c Release || exit /b 1
echo.
echo Built: %CD%\dist\UberDisk\UberDisk.exe   (portable)
echo        %CD%\dist\UberDisk-Setup.exe      (installer)
