# Creates local development secrets for SmartBank.API (never written to the repository).
# Values live in the per-user "user-secrets" store and are only read when ASPNETCORE_ENVIRONMENT=Development.
# Existing values are kept; pass -Rotate to replace them.
param([switch]$Rotate)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\SmartBank.API\SmartBank.API.csproj"

function New-RandomBase64([int]$byteCount) {
    $bytes = New-Object byte[] $byteCount
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    [Convert]::ToBase64String($bytes)
}

$existing = (dotnet user-secrets list --project $project) -join "`n"

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
    Write-Host "[set]    $name"
}

Write-Host "Done. Optional: dotnet user-secrets set GeminiSettings:ApiKey <your-key> --project src/SmartBank.API"
