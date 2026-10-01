#!/usr/bin/env bash
# Run one Vulkan render driver with the Khronos validation layer forced on, and fail if the
# layer reported any error. Used by the Vulkan lane in .github/workflows/validation-render.yml
# (Mesa lavapipe under xvfb), and runnable the same way on any Linux box with a Vulkan driver.
#
#   validation/run-with-vk-validation.sh <label> [--expect-red <regex>] -- <command...>
#
# Exit 0 only when ALL of these hold:
#   * the command exited 0,
#   * the layer was really loaded (its log file exists; it is created at vkCreateInstance),
#   * the layer's log has no "Validation Error".
# A missing log fails CLOSED: "the layer silently did not load" must never read as "no errors".
#
# --expect-red <regex> inverts the verdict for a positive control: exit 0 only if the run was
# RED (command failed, or the layer reported an error) AND <regex> (grep -E) matches the
# driver's output or the layer log, so the control proves the gate fails for the planted
# reason and not for an unrelated crash. The layer-must-load check is NOT inverted.
#
# Why the layer: a CPU driver can accept things a GPU driver rejects. The layer judges each
# API call and each SPIR-V module against the Vulkan spec for the instance's API version,
# so a green run here means "in spec", not just "lavapipe tolerated it".
set -uo pipefail

label="${1:?usage: run-with-vk-validation.sh <label> [--expect-red <regex>] -- <command...>}"
shift
expect_red=""
if [ "${1:-}" = "--expect-red" ]; then expect_red="${2:?--expect-red needs a regex}"; shift 2; fi
[ "${1:-}" = "--" ] && shift
[ $# -gt 0 ] || { echo "::error::run-with-vk-validation.sh: no command given"; exit 2; }

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
log_dir="$repo_root/validation/output/vk-validation"
mkdir -p "$log_dir"
log="$log_dir/$label.log"
out="$log_dir/$label.stdout.txt"
settings_dir="$log_dir/settings-$label"
rm -f "$log"
mkdir -p "$settings_dir"

# The settings file is the long-standing configuration route; the env vars are the newer
# layer-settings route. Both are set so the config holds across validation-layer versions.
cat > "$settings_dir/vk_layer_settings.txt" <<EOF
khronos_validation.debug_action = VK_DBG_LAYER_ACTION_LOG_MSG
khronos_validation.report_flags = error,warn
khronos_validation.log_filename = $log
EOF

export VK_INSTANCE_LAYERS=VK_LAYER_KHRONOS_validation
export VK_LAYER_SETTINGS_PATH="$settings_dir"
export VK_KHRONOS_VALIDATION_DEBUG_ACTION=VK_DBG_LAYER_ACTION_LOG_MSG
export VK_KHRONOS_VALIDATION_REPORT_FLAGS=error,warn
export VK_KHRONOS_VALIDATION_LOG_FILENAME="$log"

echo "[vk-validation] $label: $*"
"$@" 2>&1 | tee "$out"
rc=${PIPESTATUS[0]}

if [ ! -f "$log" ]; then
  echo "::error::[$label] Khronos validation layer did not load (no log at $log). Failing closed."
  exit 1
fi

errors=$(grep -c "Validation Error" "$log" || true)
warnings=$(grep -c "Validation Warning" "$log" || true)
echo "[vk-validation] $label: exit $rc, $errors validation error(s), $warnings warning(s), log $log"
if [ "$errors" -gt 0 ] || [ "$warnings" -gt 0 ]; then
  # Show the distinct messages, not thousands of per-frame repeats.
  grep -E "Validation (Error|Warning)" "$log" | sed -E 's/Object [0-9]+:.*//' | sort | uniq -c | sort -rn | head -40
fi

red=0
[ "$rc" -ne 0 ] && red=1
[ "$errors" -gt 0 ] && red=1

if [ -n "$expect_red" ]; then
  if [ "$red" -eq 1 ] && grep -qE "$expect_red" "$out" "$log"; then
    echo "[vk-validation] $label: positive control turned the gate RED for the planted reason (/$expect_red/), as required."
    exit 0
  fi
  if [ "$red" -eq 1 ]; then
    echo "::error::[$label] positive control went red, but not for the planted reason (/$expect_red/ not found)."
  else
    echo "::error::[$label] positive control stayed GREEN: the gate cannot see this class of defect."
  fi
  exit 1
fi

if [ "$rc" -ne 0 ]; then
  echo "::error::[$label] driver exited $rc"
fi
if [ "$errors" -gt 0 ]; then
  echo "::error::[$label] Khronos validation layer reported $errors error(s)"
fi
exit "$red"
