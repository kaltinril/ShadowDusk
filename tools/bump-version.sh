#!/usr/bin/env bash
# Version bump for a release, in one step (issue #483):
#   1. Directory.Build.props <Version>            (the single source of truth)
#   2. ShadowDusk.* ranges in every *packages*.lock.json (a normal or --locked-mode restore
#      never catches a stale one, issues #258/#290)
#   3. CHANGELOG.md: [Unreleased] -> dated [<version>], fresh empty [Unreleased], link refs
#   4. RELEASING.md version examples
#   5. tools/check-lock-files.sh (locked restore of every lock file; slow, skip with --skip-lock-check)
#
# Usage: tools/bump-version.sh <new-version> [--skip-lock-check]
# Run from a clean tree on the release branch. Does not commit.
set -euo pipefail
cd "$(dirname "$0")/.."

new=${1:-}
skip_check=0
[ "${2:-}" = "--skip-lock-check" ] && skip_check=1
if ! [[ "$new" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "usage: tools/bump-version.sh <major.minor.patch> [--skip-lock-check]" >&2; exit 2
fi

old=$(grep -oE '<Version>[^<]+' Directory.Build.props | head -1 | sed 's/<Version>//')
[ -n "$old" ] || { echo "no <Version> in Directory.Build.props" >&2; exit 1; }
[ "$old" != "$new" ] || { echo "Directory.Build.props already at $new" >&2; exit 1; }
grep -q "^## \[Unreleased\]" CHANGELOG.md || { echo "CHANGELOG.md has no [Unreleased] section" >&2; exit 1; }
grep -q "^## \[$new\]" CHANGELOG.md && { echo "CHANGELOG.md already has [$new]" >&2; exit 1; }
today=$(date +%Y-%m-%d)
oldre=${old//./\\.}

# 1. version
sed -i -E "s#<Version>$oldre</Version>#<Version>$new</Version>#" Directory.Build.props
grep -q "<Version>$new</Version>" Directory.Build.props || { echo "Directory.Build.props rewrite failed" >&2; exit 1; }

# 2. lock files
git ls-files '*packages*.lock.json' | xargs sed -i -E "s/(\"ShadowDusk\.[A-Za-z.]+\": \"\[)$oldre(, \)\")/\1$new\2/"
stale=$(git grep -h -o -E '"ShadowDusk\.[A-Za-z.]+": "\[[0-9.]+, \)"' -- '*packages*.lock.json' | grep -v "\[$new, )" || true)
if [ -n "$stale" ]; then
  echo "lock files still hold another ShadowDusk.* version:" >&2; echo "$stale" | sort | uniq -c >&2; exit 1
fi

# 3. CHANGELOG
tmp=$(mktemp)
awk -v new="$new" -v today="$today" '
  function flush_head() {
    print "## [Unreleased]"; print ""; print "### Added"; print ""; print "### Changed"; print ""; print "### Fixed"; print ""
    print "## [" new "] - " today
  }
  /^## \[Unreleased\]/ && !done { flush_head(); in_rel=1; done=1; next }
  in_rel && /^## \[/ {
    if (!has_content) { print ""; print "### Changed"; print ""; print "- Version bump and documentation updates"; print "" }
    in_rel=0
  }
  in_rel && $0 !~ /^[[:space:]]*$/ && $0 !~ /^### / { has_content=1 }
  { print }
' CHANGELOG.md > "$tmp"
# The awk above left an empty-section [new] block only when it had no bullets; collapse the blank run it leaves.
cat -s "$tmp" > CHANGELOG.md; rm -f "$tmp"
sed -i -E "s#^\[Unreleased\]: (.*)/compare/v$oldre\.\.\.HEAD#[Unreleased]: \1/compare/v$new...HEAD\n[$new]: \1/compare/v$old...v$new#" CHANGELOG.md
grep -q "^\[$new\]: " CHANGELOG.md || { echo "CHANGELOG link refs not updated (no [Unreleased] compare link for v$old)" >&2; exit 1; }

# 4. RELEASING.md examples
sed -i "s/$oldre/$new/g" RELEASING.md

echo "Bumped $old -> $new (Directory.Build.props, lock files, CHANGELOG.md, RELEASING.md)."

# 5. lock check
if [ "$skip_check" -eq 1 ]; then
  echo "SKIPPED tools/check-lock-files.sh (--skip-lock-check). Run it before committing."
else
  tools/check-lock-files.sh
fi
