@echo off
title SmartBank API Server
echo ==============================================
echo  SmartBank Destek Hub API Sunucusu Baslatiliyor
echo ==============================================
echo.
dotnet run --project src/SmartBank.API/SmartBank.API.csproj --launch-profile http
if %errorlevel% neq 0 (
    echo.
    echo [HATA] Sunucu baslatilamadi. Lutfen .NET SDK'nin yuklu oldugundan emin olun.
    pause
)
