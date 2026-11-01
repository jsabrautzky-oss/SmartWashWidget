@echo off
setlocal
chcp 65001 >nul

rem ===========================================================================
rem  SmartWashWidget - Release-Publish + Installer
rem
rem  1) Single-File-Publish (self-contained, kein installiertes .NET noetig)
rem       -> publish\SmartWashWidget.exe
rem  2) Inno-Setup-Installer (falls Inno Setup installiert ist)
rem       -> dist\SmartWashWidget-Setup-1.0.0.exe
rem ===========================================================================

cd /d "%~dp0"

echo.
echo [1/2] Veroeffentlichen: Single-File-Build (win-x64, self-contained) ...
dotnet publish "SmartWashWidget\SmartWashWidget.csproj" -c Release -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -o publish
if errorlevel 1 goto :fail

for %%F in ("publish\SmartWashWidget.exe") do echo     Ergebnis: %%~fF  [%%~zF Bytes]

echo.
echo [2/2] Installer ...

set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" goto :inno
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" goto :inno
set "ISCC=C:\Program Files\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" goto :inno

echo     Inno Setup wurde nicht gefunden - Installer wurde uebersprungen.
echo     Der Single-File-Build in publish\ laeuft auch ohne Installation.
goto :done

:inno
"%ISCC%" "installer\setup.iss"
if errorlevel 1 goto :fail
for %%F in ("dist\SmartWashWidget-Setup-1.0.0.exe") do echo     Installer: %%~fF  [%%~zF Bytes]

:done
echo.
echo Fertig.
endlocal
exit /b 0

:fail
echo.
echo Der Vorgang ist fehlgeschlagen.
endlocal
exit /b 1
