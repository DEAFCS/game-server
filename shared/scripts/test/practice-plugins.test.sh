#!/bin/bash
# DEAFCS: which node plugins a practice server loads.
# Run: bash shared/scripts/test/practice-plugins.test.sh
#
# The node volume (/opt/5stack/custom-plugins) carries DEAFCS's accessibility
# plugins and the skins plugin. A match server loads all of them; a practice
# server keeps the accessibility plugins and drops UTILITY_PRACTICE_SKIP_PLUGINS,
# without ever deleting anything from the node volume.

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "${SCRIPT_DIR}/../../.." && pwd)"
source "${SCRIPT_DIR}/../util.sh"

SKIP_BLOCK="$(sed -n '/^# DEAFCS: a practice server keeps the node/,/^# The practice HUD.s addon/p' \
  "${REPO_DIR}/apps/swiftly/scripts/setup.sh")"

failures=0
workdir=""

fail() {
  echo "  FAIL: $1" >&2
  failures=$((failures + 1))
}

assert_present() {
  if [ ! -e "$1" ]; then
    fail "$2"
  fi
}

assert_missing() {
  if [ -e "$1" ] || [ -L "$1" ]; then
    fail "$2"
  fi
}

# Mirrors the node folder as listed read-only on 2026-10-15.
setup() {
  workdir="$(mktemp -d)"
  local node="$workdir/node"
  for plugin in DeafCS.DisableValvePremute InventorySimulator Mutesound; do
    mkdir -p "$node/addons/swiftlys2/plugins/$plugin"
    echo "$plugin" > "$node/addons/swiftlys2/plugins/$plugin/$plugin.dll"
  done
  # The image copies SwiftlyS2's own addons in first, so plugins/ is a real
  # directory in the instance.
  mkdir -p "$workdir/instance/game/csgo/addons/swiftlys2/plugins"
}

teardown() {
  rm -rf "$workdir"
}

plugin() {
  printf '%s' "$workdir/instance/game/csgo/addons/swiftlys2/plugins/$1"
}

run_setup() {
  INSTANCE_SERVER_DIR="$workdir/instance"
  create_symlinks "$workdir/node" "$INSTANCE_SERVER_DIR/game/csgo"
  eval "$SKIP_BLOCK" > /dev/null
}

if [ -z "$SKIP_BLOCK" ]; then
  fail "setup.sh has no practice skip block"
fi

echo "a match server loads every node plugin"
setup
INSTALL_UTILITY_PRACTICE_PLUGIN=false UTILITY_PRACTICE_SKIP_PLUGINS="InventorySimulator" run_setup
assert_present "$(plugin InventorySimulator)/InventorySimulator.dll" "the match server lost the skins plugin"
assert_present "$(plugin Mutesound)/Mutesound.dll" "the match server lost Mutesound"
assert_present "$(plugin DeafCS.DisableValvePremute)/DeafCS.DisableValvePremute.dll" "the match server lost DisableValvePremute"
teardown

echo "a practice server keeps the accessibility plugins and drops skins"
setup
INSTALL_UTILITY_PRACTICE_PLUGIN=true UTILITY_PRACTICE_SKIP_PLUGINS="InventorySimulator" run_setup
assert_missing "$(plugin InventorySimulator)" "the practice server still loads the skins plugin"
assert_present "$(plugin Mutesound)/Mutesound.dll" "the practice server lost Mutesound"
assert_present "$(plugin DeafCS.DisableValvePremute)/DeafCS.DisableValvePremute.dll" "the practice server lost DisableValvePremute"
assert_present "$workdir/node/addons/swiftlys2/plugins/InventorySimulator/InventorySimulator.dll" \
  "the skins plugin was deleted from the node volume"
teardown

echo "a practice server with no skip list loads every node plugin"
setup
INSTALL_UTILITY_PRACTICE_PLUGIN=true UTILITY_PRACTICE_SKIP_PLUGINS="" run_setup
assert_present "$(plugin InventorySimulator)/InventorySimulator.dll" "an empty skip list still dropped a plugin"
teardown

echo "a plugins directory that is itself a node link is never written through"
setup
rmdir "$workdir/instance/game/csgo/addons/swiftlys2/plugins"
ln -s "$workdir/node/addons/swiftlys2/plugins" "$workdir/instance/game/csgo/addons/swiftlys2/plugins"
INSTALL_UTILITY_PRACTICE_PLUGIN=true UTILITY_PRACTICE_SKIP_PLUGINS="InventorySimulator" run_setup
assert_present "$workdir/node/addons/swiftlys2/plugins/InventorySimulator/InventorySimulator.dll" \
  "the skins plugin was deleted from the node volume through a linked plugins directory"
teardown

PRACTICE_CFG_BLOCK="$(sed -n '/^if \[ "\$SERVER_TYPE" = "Practice" \]; then/,/^fi/p' \
  "${REPO_DIR}/apps/swiftly/scripts/setup.sh")"

echo "a practice server is relay only: no ICE, no hibernation"
if [ -z "$PRACTICE_CFG_BLOCK" ]; then
  fail "setup.sh has no practice server.cfg block"
fi
setup
BASE_SERVER_DIR="$workdir/base"
INSTANCE_SERVER_DIR="$workdir/instance"
mkdir -p "$BASE_SERVER_DIR/game/csgo/cfg" "$INSTANCE_SERVER_DIR/game/csgo/cfg"
echo "hostname base" > "$BASE_SERVER_DIR/game/csgo/cfg/server.cfg"
SERVER_TYPE=Practice eval "$PRACTICE_CFG_BLOCK"
cfg="$INSTANCE_SERVER_DIR/game/csgo/cfg/server.cfg"
grep -qx "net_option P2P_Transport_ICE_Enable 0" "$cfg" || fail "the practice server.cfg still allows ICE"
grep -qx "sv_hibernate_when_empty 0" "$cfg" || fail "the practice server.cfg lost hibernation off"
grep -qx "hostname base" "$cfg" || fail "the practice server.cfg dropped the base server.cfg"
grep -qx "hostname base" "$BASE_SERVER_DIR/game/csgo/cfg/server.cfg" && \
  ! grep -q "P2P_Transport_ICE_Enable" "$BASE_SERVER_DIR/game/csgo/cfg/server.cfg" || \
  fail "the base server.cfg on the node was written to"
teardown

if [ "$failures" -gt 0 ]; then
  echo "$failures assertion(s) failed" >&2
  exit 1
fi

echo "all practice plugin tests passed"
