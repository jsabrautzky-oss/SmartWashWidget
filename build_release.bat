@echo off
setlocal
chcp 65001 >nul

rem ===========================================================================
rem  SmartWashWidget - Release-Publish + Installer + Portable-Version
rem
rem  1) Single-File-Publish (self-contained, kein installiertes .NET noetig)
rem       -> publish\SmartWashWidget.exe
rem  2) Inno-Setup-Installer (falls Inno Setup installiert ist)
rem       -> dist\SmartWashWidget-Setup-1.0.0.exe
rem  3) Portable-Version (ZIP, ohne Installation, ohne Administratorrechte)
rem       -> dist\SmartWashWidget-Portable-1.0.0-win-x64.zip
rem ===========================================================================

cd /d "%~dp0"

echo.
echo [1/3] Veroeffentlichen: Single-File-Build (win-x64, self-contained) ...
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
echo [2/3] Installer ...

set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" goto :inno
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" goto :inno
set "ISCC=C:\Program Files\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" goto :inno

echo     Inno Setup wurde nicht gefunden - Installer wurde uebersprungen.
echo     Der Single-File-Build in publish\ laeuft auch ohne Installation.
goto :portable

:inno
"%ISCC%" "installer\setup.iss"
if errorlevel 1 goto :fail
for %%F in ("dist\SmartWashWidget-Setup-1.0.0.exe") do echo     Installer: %%~fF  [%%~zF Bytes]

:portable
echo.
echo [3/3] Portable-Version: ZIP ohne Installation ...

set "PORTABLE_STAGE=dist\portable"
set "PORTABLE_DIR=dist\portable\SmartWashWidget-1.0.0"
set "PORTABLE_ZIP=dist\SmartWashWidget-Portable-1.0.0-win-x64.zip"

if exist "%PORTABLE_STAGE%" rmdir /s /q "%PORTABLE_STAGE%"
mkdir "%PORTABLE_DIR%" >nul
copy /y "publish\SmartWashWidget.exe" "%PORTABLE_DIR%\" >nul
copy /y "README.md" "%PORTABLE_DIR%\" >nul
copy /y "LICENSE" "%PORTABLE_DIR%\" >nul

rem bsdtar (Windows 10 ab 1803 eingepasst) schreibt ZIP-Eintraege mit regulaeren
rem Slash-Namen - deshalb entpacken auch Linux, macOS und Explorer korrekt.
tar -a --format zip --options "compression-level=9" -c -f "%PORTABLE_ZIP%" -C "%PORTABLE_STAGE%" SmartWashWidget-1.0.0
if errorlevel 1 goto :fail
rmdir /s /q "%PORTABLE_STAGE%"

for %%F in ("%PORTABLE_ZIP%") do echo     Portable: %%~fF  [%%~zF Bytes]
for /f %%H in ('powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-FileHash -LiteralPath '%PORTABLE_ZIP%' -Algorithm SHA256).Hash.ToLower()"') do echo     SHA-256:  %%H
echo     Inhalt: SmartWashWidget-1.0.0\ mit SmartWashWidget.exe, README.md, LICENSE.
echo     Laeuft aus jedem Ordner, ohne Installation und ohne Administratorrechte.

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
