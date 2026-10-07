# Creates local development secrets for SmartBank.API (never written to the repository).
# Values live in the per-user "user-secrets" store and are only read when ASPNETCORE_ENVIRONMENT=Development.
# Existing values are kept; pass -Rotate to replace them.
#
# On a fresh Windows machine the default execution policy blocks scripts. Run it like this (no setting is changed):
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev-secrets.ps1
# (or once: Set-ExecutionPolicy -Scope CurrentUser RemoteSigned).
#
# Keep this file ASCII-only: Windows PowerShell 5.1 reads a file without a byte order mark in the ANSI code page.
param([switch]$Rotate)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\SmartBank.API\SmartBank.API.csproj"

function New-RandomBase64([int]$byteCount) {
    $bytes = New-Object byte[] $byteCount
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    [Convert]::ToBase64String($bytes)
}

# $ErrorActionPreference does not catch a native command that exits with an error, so every dotnet call is checked.
function Assert-NativeSuccess([string]$what) {
    if ($LASTEXITCODE -ne 0) {
        throw "$what failed (exit code $LASTEXITCODE). Is the .NET SDK installed (dotnet --version)?"
    }
}

$existing = (dotnet user-secrets list --project $project) -join "`n"
Assert-NativeSuccess "dotnet user-secrets list"

$secrets = @{
    "JwtSettings:Key"  = 48   # 48 random bytes -> 64 base64 chars (HS256 needs >= 32 bytes)
    "Encryption:Key"   = 32   # AES-256 key, exactly 32 bytes
}

foreach ($name in $secrets.Keys) {
    $alreadySet = $existing -match "(?m)^$([regex]::Escape($name))\s*="
    if ($alreadySet -and -not $Rotate) {
        Write-Host "[keep]   $name already set"
        continue
    }
    dotnet user-secrets set $name (New-RandomBase64 $secrets[$name]) --project $project | Out-Null
    Assert-NativeSuccess "dotnet user-secrets set $name"
    Write-Host "[set]    $name"
}

Write-Host "Done. Optional: dotnet user-secrets set GeminiSettings:ApiKey <your-key> --project src/SmartBank.API"
