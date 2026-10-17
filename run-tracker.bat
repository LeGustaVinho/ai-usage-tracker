@echo off
setlocal
cd /d "%~dp0"

set "RELEASE_EXE=src\AIUsageTracker\bin\Release\net10.0-windows\AIUsageTracker.exe"
set "DEBUG_EXE=src\AIUsageTracker\bin\Debug\net10.0-windows\AIUsageTracker.exe"

if exist "%RELEASE_EXE%" (
    start "" "%RELEASE_EXE%"
    exit /b 0
)

if exist "%DEBUG_EXE%" (
    start "" "%DEBUG_EXE%"
    exit /b 0
)

echo Compilando AI Usage Tracker...
dotnet build src\AIUsageTracker\AIUsageTracker.csproj -c Release
start "" "%RELEASE_EXE%"
