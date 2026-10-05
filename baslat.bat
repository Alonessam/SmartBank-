@echo off
title SmartBank API Server
echo ==============================================
echo  SmartBank Destek Hub API Sunucusu Baslatiliyor
echo ==============================================
echo.
echo Gelistirme anahtarlari kontrol ediliyor (user-secrets)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\dev-secrets.ps1"
if %errorlevel% neq 0 (
    echo.
    echo [HATA] Gelistirme anahtarlari olusturulamadi. .NET SDK'nin yuklu oldugundan emin olun.
    pause
    exit /b 1
)
echo.
dotnet run --project src/SmartBank.API/SmartBank.API.csproj --launch-profile http
if %errorlevel% neq 0 (
    echo.
    echo [HATA] Sunucu baslatilamadi. Lutfen .NET SDK'nin yuklu oldugundan emin olun.
    pause
)
