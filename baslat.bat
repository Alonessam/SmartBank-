@echo off
rem Starts the SmartBank API for local development (SQL Server LocalDB). ASCII only on purpose: cmd.exe reads this file
rem in the OEM code page. Needs the .NET SDK and SQL Server LocalDB (installed with Visual Studio or SQL Server Express LocalDB).
title SmartBank API Server
cd /d "%~dp0"

echo ==============================================
echo  SmartBank API - local development start
echo ==============================================
echo.

echo [1/4] Development keys (user-secrets)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\dev-secrets.ps1"
if errorlevel 1 (
    echo.
    echo [ERROR] Could not create the development keys. Is the .NET SDK installed? ^(dotnet --version^)
    pause
    exit /b 1
)

echo.
echo [2/4] Restoring the dotnet-ef tool...
dotnet tool restore
if errorlevel 1 (
    echo.
    echo [ERROR] dotnet tool restore failed.
    pause
    exit /b 1
)

echo.
echo [3/4] Creating or updating the LocalDB database...
dotnet ef database update --project src/SmartBank.Infrastructure --startup-project src/SmartBank.API
if errorlevel 1 (
    echo.
    echo [ERROR] The database could not be created. Is SQL Server LocalDB installed? ^(sqllocaldb info^)
    echo         Without LocalDB, use the PostgreSQL route in the README ^(docker compose up -d db^).
    pause
    exit /b 1
)

echo.
echo [4/4] Starting the API on http://localhost:5038 (Ctrl+C stops it)...
dotnet run --project src/SmartBank.API/SmartBank.API.csproj --launch-profile http
rem Ctrl+C ends dotnet run with a non-zero code, so only a failure within the first seconds would matter; nothing to report here.
