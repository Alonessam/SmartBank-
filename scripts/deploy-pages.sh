#!/usr/bin/env bash
# Publishes the static frontend (src/SmartBank.Web) as the GitHub Pages site (the gh-pages branch).
# Bash equivalent of deploy-pages.ps1; read the header of that file for the reasoning.
#
#   ./scripts/deploy-pages.sh                # dry run: prepares the commit in a temporary worktree and shows the diff
#   ./scripts/deploy-pages.sh --push         # publish to origin/gh-pages
#   --version 1.3.0                          # cache-buster; default: latest git tag without the leading "v", else 1.3.0
#   --force                                  # skip the clean-tree and "on main" checks
set -euo pipefail

push=0; force=0; version=""
while [ $# -gt 0 ]; do
  case "$1" in
    --push) push=1 ;;
    --force) force=1 ;;
    --version) version="${2:?--version needs a value}"; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

repo="$(git rev-parse --show-toplevel)"
branch="$(git -C "$repo" rev-parse --abbrev-ref HEAD)"

if [ "$force" -eq 0 ]; then
  if [ -n "$(git -C "$repo" status --porcelain)" ]; then
    echo "The working tree has uncommitted changes. Commit or stash them first (or pass --force)." >&2
    exit 1
  fi
  if [ "$branch" != "main" ]; then
    echo "You are on '$branch', not on main. Check out main first (or pass --force)." >&2
    exit 1
  fi
fi

if [ -z "$version" ]; then
  version="1.3.0"
  if tag="$(git -C "$repo" describe --tags --abbrev=0 2>/dev/null)"; then version="${tag#v}"; fi
fi
echo "Publishing version $version from branch $branch."

tmp="$(mktemp -d)"
worktree="$tmp/pages"
cleanup() { git -C "$repo" worktree remove --force "$worktree" >/dev/null 2>&1 || true; rm -rf "$tmp"; }
trap cleanup EXIT

git -C "$repo" fetch --quiet origin gh-pages
git -C "$repo" worktree add --detach "$worktree" origin/gh-pages >/dev/null

# The site is exactly the contents of the Web folder: start from an empty tree so removed files disappear.
find "$worktree" -mindepth 1 -maxdepth 1 ! -name .git -exec rm -rf {} +
cp -R "$repo/src/SmartBank.Web/." "$worktree/"

# Cache busters on the scripts, so browsers fetch the new JavaScript instead of a cached copy.
for page in "$worktree"/*.html; do
  sed -E -i.bak "s/((app|chat)\.js)(\?v=[^\"]*)?\"/\1?v=$version\"/g" "$page"
  rm -f "$page.bak"
done

git -C "$worktree" add -A
if git -C "$worktree" diff --cached --quiet; then
  echo "The Pages site already matches this version. Nothing to publish."
  exit 0
fi

git -C "$worktree" commit --quiet -m "Deploy SmartBank v$version static site"
echo "Changes against the currently published site:"
git -C "$worktree" diff --stat HEAD~1 HEAD

if [ "$push" -eq 1 ]; then
  git -C "$worktree" push origin HEAD:gh-pages
  echo "Published. GitHub Pages usually updates within a minute or two."
else
  echo "Dry run: nothing was pushed. Re-run with --push to publish."
fi
