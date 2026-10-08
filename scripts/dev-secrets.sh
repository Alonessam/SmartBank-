#!/usr/bin/env bash
# Creates local development secrets for SmartBank.API (never written to the repository).
# Bash equivalent of dev-secrets.ps1 for Linux and macOS. Values live in the per-user "user-secrets" store and are only
# read when ASPNETCORE_ENVIRONMENT=Development. Existing values are kept; pass --rotate to replace them.
set -euo pipefail

rotate=0
case "${1:-}" in
  "") ;;
  --rotate) rotate=1 ;;
  *) echo "Unknown argument '$1'. Usage: dev-secrets.sh [--rotate]" >&2; exit 2 ;;
esac

project="$(cd "$(dirname "$0")/.." && pwd)/src/SmartBank.API/SmartBank.API.csproj"
existing="$(dotnet user-secrets list --project "$project" || true)"

set_secret() {
  local name="$1" bytes="$2"
  if [ "$rotate" -eq 0 ] && printf '%s\n' "$existing" | grep -Eq "^${name} *="; then
    echo "[keep]   $name already set"
    return
  fi
  local value
  value="$(head -c "$bytes" /dev/urandom | base64 | tr -d '\n')"
  dotnet user-secrets set "$name" "$value" --project "$project" > /dev/null
  echo "[set]    $name"
}

set_secret "JwtSettings:Key" 48   # 48 random bytes -> 64 base64 chars (HS256 needs >= 32 bytes)
set_secret "Encryption:Key" 32    # AES-256 key, exactly 32 bytes

echo "Done. Optional: dotnet user-secrets set GeminiSettings:ApiKey <your-key> --project src/SmartBank.API"
