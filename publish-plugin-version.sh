#!/usr/bin/env bash
# Interactive release helper: bumps PfExplorer.csproj's <Version>, commits,
# tags, and pushes both — pushing the tag is what release.yml actually
# reacts to (see .github/workflows/release.yml), so this script's whole job
# is producing that tag correctly, not building/publishing anything itself.
#
# Works the same whether run directly or via the repo-root symlink to this
# file — resolves its own real path first so relative operations (reading/
# writing PfExplorer.csproj, git commands) always target this directory.
set -euo pipefail

SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
SCRIPT_DIR="$(cd "$(dirname "$SCRIPT_PATH")" && pwd)"
cd "$SCRIPT_DIR"

if [[ -n "$(git status --porcelain)" ]]; then
  echo "client/PfExplorer has uncommitted changes — commit or stash them first" >&2
  echo "so the version-bump commit only contains the version bump." >&2
  exit 1
fi

CSPROJ="PfExplorer.csproj"
current_version="$(grep -oP '(?<=<Version>)[^<]+' "$CSPROJ")"
IFS='.' read -r major minor patch _revision <<< "$current_version"
current_tag="v${current_version%.0}"

echo "Current version: $current_version"
echo "  0) retrigger — re-run release.yml for $current_tag, no version change"
echo "Bump:"
echo "  1) patch  ($major.$minor.$((patch + 1)).0)"
echo "  2) minor  ($major.$((minor + 1)).0.0)"
echo "  3) major  ($((major + 1)).0.0.0)"
read -rp "Choice [0-3]: " bump_choice

if [[ "$bump_choice" == "0" ]]; then
  # GitHub's tag-push trigger only fires on an actual push event — pushing
  # an unchanged existing tag is a no-op, nothing re-runs. Force-deleting
  # the tag (local + remote) and recreating it at the current HEAD is what
  # actually produces a new push for CI to react to — and lands it on
  # whatever's currently committed, so if the last run failed because of a
  # workflow bug (not the plugin code itself), the fix for that bug — once
  # committed — comes along for free instead of re-running the exact same
  # broken workflow that was there when the tag was first pushed.
  echo
  echo "Retriggering $current_tag at $(git rev-parse --short HEAD)..."
  git push origin ":refs/tags/$current_tag" || true
  git tag -d "$current_tag" 2>/dev/null || true
  git tag -a "$current_tag" -m "Retrigger"
  git push origin HEAD:main
  git push origin "$current_tag"
  echo
  echo "Pushed $current_tag again — release.yml will re-run now."
  exit 0
fi

case "$bump_choice" in
  1) new_version="$major.$minor.$((patch + 1)).0" ;;
  2) new_version="$major.$((minor + 1)).0.0" ;;
  3) new_version="$((major + 1)).0.0.0" ;;
  *) echo "Invalid choice." >&2; exit 1 ;;
esac

read -rp "Release description: " description
if [[ -z "$description" ]]; then
  echo "Description can't be empty." >&2
  exit 1
fi

# release.yml checks the pushed tag against this field, so it has to be the
# actual source of truth, not something derived separately and hoped to match.
sed -i "s#<Version>$current_version</Version>#<Version>$new_version</Version>#" "$CSPROJ"

tag="v${new_version%.0}" # trim the always-0 4th (AssemblyVersion revision) segment for the tag

echo
echo "Bumping $current_version -> $new_version, tagging $tag"
git add "$CSPROJ"
git commit -m "Release $tag: $description"
git tag -a "$tag" -m "$description"

git push origin HEAD:main
git push origin "$tag"

echo
echo "Pushed $tag — release.yml will build and publish it now."
