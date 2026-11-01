@echo off
setlocal

rem ===========================================================================
rem  SmartWashWidget starten
rem  Nutzt den Single-File-Build (publish), sonst den Release-, sonst den
rem  Debug-Build; fehlt alles, wird zuerst gebaut.
rem
rem  Zusaetzliche Argumente werden durchgereicht:
rem     run.bat --demo       Live-Demo mit animierten Schein-Laeufen
rem     run.bat --live       Live-Modus gegen die SmartThings-API
rem     run.bat --settings   Einstellungsfenster direkt nach dem Start
rem     run.bat --reset      Konfiguration auf Werkszustand zuruecksetzen
rem ===========================================================================

cd /d "%~dp0"

set "PUB=publish\SmartWashWidget.exe"
set "REL=SmartWashWidget\bin\Release\net9.0-windows\win-x64\SmartWashWidget.exe"
set "DBG=SmartWashWidget\bin\Debug\net9.0-windows\win-x64\SmartWashWidget.exe"

if exist "%PUB%" goto :launch
if exist "%REL%" goto :launch
if exist "%DBG%" goto :launch

echo Baue SmartWashWidget - Debug ...
dotnet build "SmartWashWidget\SmartWashWidget.csproj" -c Debug -v minimal
if errorlevel 1 goto :fail
set "DBG=SmartWashWidget\bin\Debug\net9.0-windows\win-x64\SmartWashWidget.exe"

:launch
if exist "%PUB%" set "TARGET=%PUB%"
if not defined TARGET if exist "%REL%" set "TARGET=%REL%"
if not defined TARGET if exist "%DBG%" set "TARGET=%DBG%"
if not defined TARGET goto :fail

echo Starte %TARGET% %*
start "" "%TARGET%" %*
endlocal
exit /b 0

:fail
echo.
echo Build fehlgeschlagen - bitte 'dotnet --version' pruefen, .NET 9 SDK wird benoetigt.
endlocal
exit /b 1
