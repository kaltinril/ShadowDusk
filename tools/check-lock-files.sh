#!/usr/bin/env bash
# Locked-mode restore of every project that owns a tracked NuGet lock file, including the
# ones outside ShadowDusk.slnx (validation drivers, browser probes, phase appendices).
#
# Why: `dotnet restore ShadowDusk.slnx --locked-mode` only covers solution projects. A
# Directory.Packages.props change that misses an out-of-solution lock file stays green on
# a normal PR and only fails later, inside a label-gated job or a hand-run gate (issue #291:
# Vortice.Dxc went to [3.3.4] and Vkd3dCorpusProbe's lock file kept [3.3.4, )).
#
# Versioned lock files (packages.<KniVersion>.lock.json, validation/KniXnbContentLoad) are
# restored with -p:KniVersion=<version>. Projects whose prerequisites are not present on
# this host are reported as SKIP, never silently passed:
#   - net*-android projects need the android workload;
#   - validation/FnaValidation needs the external/FNA checkout (restore-fna.ps1).
#
# Usage: tools/check-lock-files.sh   (exits non-zero on any mismatch)
set -uo pipefail
cd "$(dirname "$0")/.."

fail=0
while IFS= read -r lock; do
  dir=$(dirname "$lock")
  proj=$(ls "$dir"/*.csproj 2>/dev/null | head -1)
  if [ -z "$proj" ]; then
    echo "FAIL  $lock: no .csproj beside it"; fail=1; continue
  fi
  if grep -q '<TargetFramework>[^<]*-android' "$proj" && ! dotnet workload list 2>/dev/null | grep -q '^android'; then
    echo "SKIP  $lock: android workload not installed"; continue
  fi
  if grep -q 'external\\FNA\\FNA.Core.csproj' "$proj" && [ ! -f "$dir/external/FNA/FNA.Core.csproj" ]; then
    echo "SKIP  $lock: external/FNA checkout absent"; continue
  fi
  extra=()
  name=$(basename "$lock")
  if [ "$name" != "packages.lock.json" ]; then
    v=${name#packages.}; v=${v%.lock.json}
    extra=("-p:KniVersion=$v")
  fi
  # EnableWindowsTargeting lets a Linux/macOS host restore the net*-windows drivers.
  if out=$(dotnet restore "$proj" --locked-mode -p:EnableWindowsTargeting=true ${extra[@]+"${extra[@]}"} 2>&1); then
    echo "OK    $lock"
  else
    echo "FAIL  $lock"; echo "$out" | grep -E 'error' | head -5; fail=1
  fi
done < <(git ls-files '*packages*.lock.json')

if [ "$fail" -ne 0 ]; then
  echo "Lock-file check FAILED. Regenerate with: dotnet restore <project> --force-evaluate (add -p:KniVersion=<v> for a versioned lock file)."
fi
exit "$fail"
