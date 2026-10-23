#!/bin/bash
# Regression tests for the practice HUD workshop-addon helpers in util.sh.
# Run: bash shared/scripts/test/workshop-addons.test.sh
#
# DEAFCS: taken verbatim from upstream 5Stack shared/scripts/test/util.test.sh
# (7658cc8): its assert helpers, fixture setup/teardown and the AddonsManager
# section. The rest of that file covers upstream helpers DEAFCS does not ship.
set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${SCRIPT_DIR}/../util.sh"

failures=0
workdir=""

fail() {
  echo "  FAIL: $1" >&2
  failures=$((failures + 1))
}

assert_equals() {
  if [ "$1" != "$2" ]; then
    fail "$3 (expected '$2', got '$1')"
  fi
}

assert_missing() {
  if [ -e "$1" ]; then
    fail "$2"
  fi
}

setup() {
  workdir="$(mktemp -d)"

  # Stands in for /opt/5stack/custom-plugins: managed installs and hand-placed
  # files share this one directory now, which is what the index exists to tell
  # apart.
  mkdir -p "$workdir/plugins/addons/swiftlys2/configs/plugins/Existing"
  echo "SHARED-ORIGINAL" > "$workdir/plugins/addons/swiftlys2/configs/plugins/Existing/config.jsonc"

  mkdir -p "$workdir/plugins/addons/swiftlys2/plugins/InventorySimulator"
  echo "INVSIM" > "$workdir/plugins/addons/swiftlys2/plugins/InventorySimulator/InventorySimulator.dll"

  mkdir -p "$workdir/plugins/addons/swiftlys2/plugins/Retakes"
  echo "RETAKES" > "$workdir/plugins/addons/swiftlys2/plugins/Retakes/Retakes.dll"

  # Absent from the index: dropped in by hand, so it always loads.
  mkdir -p "$workdir/plugins/addons/swiftlys2/plugins/HandPlaced"
  echo "HAND" > "$workdir/plugins/addons/swiftlys2/plugins/HandPlaced/HandPlaced.dll"

  mkdir -p "$workdir/plugins/.5stack-plugins"
  printf 'inventory-simulator\t3.1.0\taddons/swiftlys2/plugins/InventorySimulator/InventorySimulator.dll\n' \
    > "$workdir/plugins/.5stack-plugins/index"
  printf 'retakes\t1.2.0\taddons/swiftlys2/plugins/Retakes/Retakes.dll\n' \
    >> "$workdir/plugins/.5stack-plugins/index"

  # The instance starts with a directory symlink pointing back at the shared
  # volume, which is the shape that used to let a later pass write through it
  # and mutate node-wide state. Only this one directory is pre-linked -- linking
  # the whole tree up front would mask whether link_plugins gates anything.
  mkdir -p "$workdir/instance/game/csgo/addons/swiftlys2"
  ln -s "$workdir/plugins/addons/swiftlys2/configs" \
    "$workdir/instance/game/csgo/addons/swiftlys2/configs"
}

teardown() {
  if [ -n "$workdir" ]; then
    rm -rf "$workdir"
  fi
}

addons_manager_config() {
  printf '%s' "$workdir/instance/game/csgo/addons/swiftlys2/configs/plugins/AddonsManager/config.jsonc"
}

echo "workshop_addon_ids keeps each workshop id once, in the order given"
assert_equals "$(workshop_addon_ids "3791548068" "100,3791548068,,200" "" 2>/dev/null | tr '\n' ' ')" \
  "3791548068 100 200 " "the ids were not deduplicated in order"

# AddonsManager validates its config on start and drops all of it over one bad
# entry, so nothing that is not a bare number may reach it.
echo "workshop_addon_ids drops anything that is not a bare workshop id"
assert_equals "$(workshop_addon_ids '100,"200",3 4,*,https://x/?id=5' 2>/dev/null | tr '\n' ' ')" \
  "100 " "a malformed id was passed through"
case "$(workshop_addon_ids '"200"' 2>&1 >/dev/null)" in
  *"'\"200\"' is not a workshop id"*) ;;
  *) fail "a malformed id was dropped without saying so" ;;
esac

echo "enable_addons_manager links AddonsManager and serves exactly the ids given"
setup
mkdir -p "$workdir/addons-manager/AddonsManager"
ADDONS_MANAGER_DIR="$workdir/addons-manager/AddonsManager" \
  NODE_PLUGINS_DIR="$workdir/no-node-plugins" CUSTOM_PLUGINS_DIR="$workdir/plugins" \
  enable_addons_manager "$workdir/instance/game/csgo" 100 200 > /dev/null 2>&1
assert_equals "$(readlink "$workdir/instance/game/csgo/addons/swiftlys2/plugins/AddonsManager")" \
  "$workdir/addons-manager/AddonsManager" "AddonsManager was not linked into the server"
assert_equals "$(jq -c '.Main' "$(addons_manager_config)" 2>/dev/null)" \
  '{"Addons":["100","200"],"RedownloadAddonOnMount":true}' "the config does not serve the ids given"
assert_missing "$workdir/plugins/addons/swiftlys2/configs/plugins/AddonsManager" \
  "one server's addons were written onto the node volume"
teardown

echo "enable_addons_manager replaces what an earlier boot served"
setup
mkdir -p "$workdir/addons-manager/AddonsManager"
ADDONS_MANAGER_DIR="$workdir/addons-manager/AddonsManager" \
  NODE_PLUGINS_DIR="$workdir/no-node-plugins" CUSTOM_PLUGINS_DIR="$workdir/plugins" \
  enable_addons_manager "$workdir/instance/game/csgo" 100 200 > /dev/null 2>&1
ADDONS_MANAGER_DIR="$workdir/addons-manager/AddonsManager" \
  NODE_PLUGINS_DIR="$workdir/no-node-plugins" CUSTOM_PLUGINS_DIR="$workdir/plugins" \
  enable_addons_manager "$workdir/instance/game/csgo" 300 > /dev/null 2>&1
assert_equals "$(jq -c '.Main.Addons' "$(addons_manager_config)" 2>/dev/null)" '["300"]' \
  "an earlier boot's addons were still served"
teardown

echo "enable_addons_manager leaves the server alone in an image without AddonsManager"
setup
ADDONS_MANAGER_DIR="$workdir/no-addons-manager" \
  enable_addons_manager "$workdir/instance/game/csgo" 100 > /dev/null 2>&1
assert_equals "$?" "0" "a missing AddonsManager should not abort setup"
assert_missing "$workdir/instance/game/csgo/addons/swiftlys2/plugins/AddonsManager" \
  "a link to a missing AddonsManager was planted"
assert_missing "$(addons_manager_config)" "a config was written for a plugin that is not there"
teardown

if [ "$failures" -gt 0 ]; then
  echo "$failures assertion(s) failed" >&2
  exit 1
fi

echo "all workshop addon tests passed"
