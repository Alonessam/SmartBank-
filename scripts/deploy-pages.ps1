# Publishes the static frontend (src/SmartBank.Web) as the GitHub Pages site, i.e. the gh-pages branch.
#
# Run it AFTER the matching API is live: the v1.1 frontend talks to the v1.1 API (role in the sign-in response, the
# two-step password reset) and the old API does not know those endpoints.
#
# Without -Push it only prepares the commit in a temporary worktree, prints what would change and cleans up, so it is
# safe to try. With -Push it publishes to origin/gh-pages (which updates the live demo).
#
#   ./scripts/deploy-pages.ps1            # dry run
#   ./scripts/deploy-pages.ps1 -Push      # publish
param(
    [string]$Version = "1.2.0",
    [switch]$Push
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
$source = Join-Path $repo "src\SmartBank.Web"
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
        $text = [regex]::Replace($text, '((?:app|chat)\.js)(\?v=[^"]*)?"', "`$1?v=$Version`"")
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
