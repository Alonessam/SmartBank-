# Publishes the static frontend (src/SmartBank.Web) as the GitHub Pages site, i.e. the gh-pages branch.
#
# Run it AFTER the matching API is live: the frontend talks to the API of the same release (sign-in response, refresh
# tokens, endpoints) and an older API may not know them.
#
# Safety rules (override with -Force only when you know why):
#   - the working tree must be clean (no uncommitted changes), because the site is built from src/SmartBank.Web as it is
#     on disk and an uncommitted edit would be published without ever being in git;
#   - the current branch must be main, and it must be exactly origin/main (after a fetch): the site is meant to show what
#     is on GitHub, not local commits that were never pushed.
#
# Without -Push it only prepares the commit in a temporary worktree, prints what would change and cleans up, so it is
# safe to try. With -Push it publishes to origin/gh-pages (which updates the live demo).
#
# -Version is the cache-buster appended to the script URLs (app.js, chat.js and styles.css ?v=...). It defaults to the latest git tag without the
# leading "v" (so tag the release first); with no tag and no -Version the script stops instead of guessing a number.
#
#   ./scripts/deploy-pages.ps1            # dry run
#   ./scripts/deploy-pages.ps1 -Push      # publish
param(
    [string]$Version,
    [switch]$Push,
    [switch]$Force
)

# Not "Stop": Windows PowerShell 5.1 turns git's normal progress messages on stderr into terminating errors.
# Every git call goes through Invoke-Git, which checks the exit code instead.
$ErrorActionPreference = "Continue"

function Invoke-Git {
    $output = & git @args 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed:`n$($output -join "`n")" }
    $output
}

$repo = (Invoke-Git rev-parse --show-toplevel | Select-Object -First 1).Trim()
$source = Join-Path (Join-Path $repo "src") "SmartBank.Web"

# --- Safety checks -----------------------------------------------------------------------------------------------
$branch = (Invoke-Git -C $repo rev-parse --abbrev-ref HEAD | Select-Object -First 1).Trim()
$dirty = @(Invoke-Git -C $repo status --porcelain)

if (-not $Force) {
    if ($dirty.Count -gt 0) {
        throw "The working tree has uncommitted changes. Commit or stash them first (or pass -Force):`n$($dirty -join "`n")"
    }
    if ($branch -ne "main") {
        throw "You are on '$branch', not on main. Check out main first (or pass -Force)."
    }

    Invoke-Git -C $repo fetch --quiet origin main | Out-Null
    $head = (Invoke-Git -C $repo rev-parse HEAD | Select-Object -First 1).Trim()
    $remote = (Invoke-Git -C $repo rev-parse origin/main | Select-Object -First 1).Trim()
    if ($head -ne $remote) {
        throw "Local main ($head) is not the same commit as origin/main ($remote). Push or pull first (or pass -Force)."
    }
}

# --- Version -----------------------------------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($Version)) {
    & git -C $repo describe --tags --abbrev=0 2>$null | ForEach-Object {
        if ($_ -match '^v?(\d+\.\d+\.\d+.*)$') { $Version = $Matches[1] }
    }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "No version: the repository has no tag like v1.3.2 and -Version was not given. Tag the release first (git tag -a v1.3.2 -m ...) or pass -Version 1.3.2."
    }
}
Write-Host "Publishing version $Version from branch $branch."

$worktree = Join-Path ([System.IO.Path]::GetTempPath()) ("smartbank-pages-" + [guid]::NewGuid().ToString("N"))

Invoke-Git -C $repo fetch --quiet origin gh-pages | Out-Null
Invoke-Git -C $repo worktree add --detach $worktree origin/gh-pages | Out-Null

try {
    # The Pages site is exactly the contents of the Web folder: start from an empty tree so removed files disappear.
    Get-ChildItem $worktree -Force | Where-Object { $_.Name -ne ".git" } | Remove-Item -Recurse -Force
    Copy-Item (Join-Path $source "*") $worktree -Recurse

    # Cache busters on the scripts, so browsers fetch the new JavaScript instead of a cached copy.
    foreach ($page in Get-ChildItem $worktree -Filter *.html) {
        $text = [System.IO.File]::ReadAllText($page.FullName)
        $text = [regex]::Replace($text, '((?:app|chat)\.js|styles\.css)(\?v=[^"]*)?"', "`$1?v=$Version`"")
        [System.IO.File]::WriteAllText($page.FullName, $text, (New-Object System.Text.UTF8Encoding($false)))
    }

    Invoke-Git -C $worktree add -A | Out-Null

    & git -C $worktree diff --cached --quiet
    if ($LASTEXITCODE -eq 0) {
        Write-Host "The Pages site already matches this version. Nothing to publish."
        return
    }

    Invoke-Git -C $worktree commit --quiet -m "Deploy SmartBank v$Version static site" | Out-Null
    Write-Host "Changes against the currently published site:"
    Invoke-Git -C $worktree diff --stat HEAD~1 HEAD

    if ($Push) {
        Invoke-Git -C $worktree push origin HEAD:gh-pages | Out-Null
        Write-Host "Published. GitHub Pages usually updates within a minute or two."
    }
    else {
        Write-Host "Dry run: nothing was pushed. Re-run with -Push to publish."
    }
}
finally {
    & git -C $repo worktree remove --force $worktree 2>&1 | Out-Null
}
