#!/bin/bash

if [ "${GAME_ID}" = "740" ]; then
  SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  bash "${SCRIPT_DIR}/setup-csgo.sh"
  exit $?
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${SCRIPT_DIR}/util.sh"

echo "---Setup Non Synlinkable Files and Directories---"

make_directories=(
  "game/csgo/cfg"
  "game/csgo/addons"
  "game/csgo/maps/soundcache"
  "game/csgo/logs"
  "game/bin/linuxsteamrt64"
)

create_directories "$INSTANCE_SERVER_DIR" "${make_directories[@]}"

cp -R "$BASE_SERVER_DIR/game/csgo/cfg" "$INSTANCE_SERVER_DIR/game/csgo"
rm "$INSTANCE_SERVER_DIR/game/csgo/cfg/server.cfg"

if [ ! -d "$BASE_SERVER_DIR/game/bin/linuxsteamrt64/steamapps" ]; then
    mkdir -p "$BASE_SERVER_DIR/game/bin/linuxsteamrt64/steamapps"
fi

for file in "$BASE_SERVER_DIR/game/bin/linuxsteamrt64"/*; do
    if [ -f "$file" ]; then
        cp "$file" "$INSTANCE_SERVER_DIR/game/bin/linuxsteamrt64/"
    elif [ -d "$file" ]; then
        if [[ "$file" != *"steamapps"* ]]; then
            cp -R "$file" "$INSTANCE_SERVER_DIR/game/bin/linuxsteamrt64/"
        fi
    fi
done

echo "---Install Addons---"
cp -r "/opt/addons" "${INSTANCE_SERVER_DIR}/game/csgo"

echo "---Create Symbolic Links---"

if [ "$SERVER_TYPE" = "Ranked" ]; then
  cp "/opt/server-cfg/ranked.server.cfg" "$INSTANCE_SERVER_DIR/game/csgo/cfg/server.cfg"
  # Never a match server: it is started for its match and stopped after it.
  if [ "$HIBERNATE_WHEN_EMPTY" = "true" ] && [ -z "$MATCH_ID" ]; then
    sed -i 's/^sv_hibernate_when_empty.*/sv_hibernate_when_empty 1/' "$INSTANCE_SERVER_DIR/game/csgo/cfg/server.cfg"
  fi
  cp "/opt/server-cfg/5stack.base.cfg" "$INSTANCE_SERVER_DIR/game/csgo/cfg"
  cp "/opt/server-cfg/5stack.competitive.cfg" "$INSTANCE_SERVER_DIR/game/csgo/cfg"
  cp "/opt/server-cfg/5stack.duel.cfg" "$INSTANCE_SERVER_DIR/game/csgo/cfg"
  cp "/opt/server-cfg/5stack.knife.cfg" "$INSTANCE_SERVER_DIR/game/csgo/cfg"
  cp "/opt/server-cfg/5stack.lan.cfg" "$INSTANCE_SERVER_DIR/game/csgo/cfg"
  cp "/opt/server-cfg/5stack.live.cfg" "$INSTANCE_SERVER_DIR/game/csgo/cfg"
  cp "/opt/server-cfg/5stack.warmup.cfg" "$INSTANCE_SERVER_DIR/game/csgo/cfg"
  cp "/opt/server-cfg/5stack.wingman.cfg" "$INSTANCE_SERVER_DIR/game/csgo/cfg"
  cp "/opt/server-cfg/valve-rulebook.cfg" "$INSTANCE_SERVER_DIR/game/csgo/cfg"
fi

# A practice server runs no match plugin to hold it awake, so the base
# server.cfg it would otherwise be linked to is copied in with hibernation off.
if [ "$SERVER_TYPE" = "Practice" ]; then
  if [ -f "$BASE_SERVER_DIR/game/csgo/cfg/server.cfg" ]; then
    cp "$BASE_SERVER_DIR/game/csgo/cfg/server.cfg" "$INSTANCE_SERVER_DIR/game/csgo/cfg/server.cfg"
  fi
  printf '\nsv_hibernate_when_empty 0\n' >> "$INSTANCE_SERVER_DIR/game/csgo/cfg/server.cfg"
  # DEAFCS: relay only, like ranked/public server.cfg (bda724a). A practice
  # server copies the base server.cfg instead, so it needs the same line to
  # share no ICE candidates and never hand out the node's real IP.
  printf 'net_option P2P_Transport_ICE_Enable 0\n' >> "$INSTANCE_SERVER_DIR/game/csgo/cfg/server.cfg"
fi

if [ ! -d "/opt/custom-plugins/addons/swiftlys2/configs" ]; then
  mkdir -p "/opt/custom-plugins/addons/swiftlys2/configs"
fi

if [ -d "${INSTANCE_SERVER_DIR}/game/csgo/addons/swiftlys2/configs" ]; then
  if [ "$(ls -A "${INSTANCE_SERVER_DIR}/game/csgo/addons/swiftlys2/configs")" ]; then
    cp -r "${INSTANCE_SERVER_DIR}/game/csgo/addons/swiftlys2/configs/." "/opt/custom-plugins/addons/swiftlys2/configs"
  fi
  rm -rf "${INSTANCE_SERVER_DIR}/game/csgo/addons/swiftlys2/configs"
fi

ln -s "/opt/custom-plugins/addons/swiftlys2/configs" "${INSTANCE_SERVER_DIR}/game/csgo/addons/swiftlys2/configs"

# Public-server treatment: every installed custom plugin gets symlinked in,
# which is what "load on every match" is meant to gate. A practice server is
# 5stack-managed like a ranked one -- it runs its own plugin and nothing else.
if [ "$SERVER_TYPE" != "Ranked" ] && [ "$SERVER_TYPE" != "Practice" ]; then
  if [ ! -d "/opt/custom-plugins" ]; then
    mkdir -p "/opt/custom-plugins"
  fi

  if [ ! -d "/opt/custom-plugins/maps" ]; then
    mkdir -p "/opt/custom-plugins/maps"
  fi

  if [ ! -e "/opt/custom-plugins/maps/mg_public.txt" ]; then
    cp "/opt/server-cfg/mg_public.txt" "/opt/custom-plugins/maps/mg_public.txt"
  fi

  if [ ! -d "/opt/custom-plugins/cfg" ]; then
    mkdir -p "/opt/custom-plugins/cfg"
  fi

  if [ ! -e "/opt/custom-plugins/cfg/server.cfg" ]; then
    cp "/opt/server-cfg/public.server.cfg" "/opt/custom-plugins/cfg/server.cfg"
  fi

  if [ ! -e "/opt/custom-plugins/addons/swiftlys2/configs/core.jsonc" ]; then
    cp "/opt/server-cfg/core.jsonc" "/opt/custom-plugins/addons/swiftlys2/configs/core.jsonc"
  fi

  create_symlinks "/opt/custom-plugins" "${INSTANCE_SERVER_DIR}/game/csgo"
fi

if $AUTOLOAD_PLUGINS = true ; then
  echo "---Install Custom Plugins---"
  create_symlinks "/opt/custom-plugins" "${INSTANCE_SERVER_DIR}/game/csgo"

  if [ -e "/opt/custom-plugins/addons/swiftlys2/gamedata/cs2/core/offsets.jsonc" ]; then
    echo "---Install Custom Gamedata---"
    rm "${INSTANCE_SERVER_DIR}/game/csgo/addons/swiftlys2/gamedata/cs2/core/offsets.jsonc"  
    cp "/opt/custom-plugins/addons/swiftlys2/gamedata/cs2/core/offsets.jsonc" "${INSTANCE_SERVER_DIR}/game/csgo/addons/swiftlys2/gamedata/cs2/core/offsets.jsonc"
  fi

  if [ -e "/opt/custom-plugins/addons/swiftlys2/gamedata/cs2/core/patches.jsonc" ]; then
    echo "---Install Custom Gamedata---"
    rm "${INSTANCE_SERVER_DIR}/game/csgo/addons/swiftlys2/gamedata/cs2/core/patches.jsonc"  
    cp "/opt/custom-plugins/addons/swiftlys2/gamedata/cs2/core/patches.jsonc" "${INSTANCE_SERVER_DIR}/game/csgo/addons/swiftlys2/gamedata/cs2/core/patches.jsonc"
  fi

  if [ -e "/opt/custom-plugins/addons/swiftlys2/gamedata/cs2/core/signatures.jsonc" ]; then
    echo "---Install Custom Gamedata---"
    rm "${INSTANCE_SERVER_DIR}/game/csgo/addons/swiftlys2/gamedata/cs2/core/signatures.jsonc"  
    cp "/opt/custom-plugins/addons/swiftlys2/gamedata/cs2/core/signatures.jsonc" "${INSTANCE_SERVER_DIR}/game/csgo/addons/swiftlys2/gamedata/cs2/core/signatures.jsonc"
  fi
fi

create_symlinks "$BASE_SERVER_DIR" "$INSTANCE_SERVER_DIR"

if $INSTALL_5STACK_PLUGIN = true ; then
  echo "---Install 5Stack---"
  FIVESTACK_PLUGIN_DIR="${INSTANCE_SERVER_DIR}/game/csgo/addons/swiftlys2/plugins/FiveStack"
  if [ "${DEV_SWAPPED}" == "1" ]; then
    # AutoHotReload's recursive FileSystemWatcher does not follow symlinked plugin
    # dirs, so hot reload needs the dev volume mounted straight onto this path
    # (see the dev deployment). Without the mount, fall back to a symlink.
    mkdir -p "$FIVESTACK_PLUGIN_DIR"
    if mountpoint -q "$FIVESTACK_PLUGIN_DIR"; then
      echo "---5Stack dev: plugin dir is a mounted volume (hot reload enabled)---"
    else
      # css and sw dev builds share the dev volume; each uses its own subfolder
      rmdir "$FIVESTACK_PLUGIN_DIR" 2>/dev/null
      mkdir -p "/opt/dev/sw"
      ln -s "/opt/dev/sw" "$FIVESTACK_PLUGIN_DIR"
      echo "---5Stack dev: symlinked /opt/dev/sw (hot reload OFF; use 'sw plugins reload FiveStack')---"
    fi
  elif [ ! -e "$FIVESTACK_PLUGIN_DIR" ]; then
    ln -s "/opt/mod" "$FIVESTACK_PLUGIN_DIR"
  else
    # dir pre-exists (dev volume mount without DEV_SWAPPED); don't plant /opt/mod inside it
    echo "---5Stack: plugin dir already present, skipping /opt/mod symlink---"
  fi
fi

# A practice server runs this instead of the match plugin, never alongside it.
if [ "${INSTALL_UTILITY_PRACTICE_PLUGIN:-false}" = "true" ]; then
  echo "---Install Utility Practice---"
  UTILITY_PRACTICE_PLUGIN_DIR="${INSTANCE_SERVER_DIR}/game/csgo/addons/swiftlys2/plugins/UtilityPractice"
  if [ "${DEV_SWAPPED}" == "1" ]; then
    # AutoHotReload's recursive FileSystemWatcher does not follow symlinked plugin
    # dirs, so hot reload needs the dev volume mounted straight onto this path
    # (see the dev deployment). Without the mount, fall back to a symlink.
    mkdir -p "$UTILITY_PRACTICE_PLUGIN_DIR"
    if mountpoint -q "$UTILITY_PRACTICE_PLUGIN_DIR"; then
      echo "---Utility Practice dev: plugin dir is a mounted volume (hot reload enabled)---"
    else
      # css and sw dev builds share the dev volume; each uses its own subfolder
      rmdir "$UTILITY_PRACTICE_PLUGIN_DIR" 2>/dev/null
      mkdir -p "/opt/dev/utility-sw"
      ln -s "/opt/dev/utility-sw" "$UTILITY_PRACTICE_PLUGIN_DIR"
      echo "---Utility Practice dev: symlinked /opt/dev/utility-sw (hot reload OFF; use 'sw plugins reload UtilityPractice')---"
    fi
  elif [ ! -e "$UTILITY_PRACTICE_PLUGIN_DIR" ]; then
    ln -s "/opt/utility-practice" "$UTILITY_PRACTICE_PLUGIN_DIR"
  else
    echo "---Utility Practice: plugin dir already present, skipping /opt/utility-practice symlink---"
  fi

  # A render pod is the only client and has no HUD to show; the addon's
  # download prompt would stall its cs2 on connect.
  if [ "${UTILITY_RENDER_MODE:-}" = "true" ]; then
    HUD_WORKSHOP_ID=""
    WORKSHOP_ADDONS=""
  fi

  # The HUD's Panorama layouts live in a workshop addon, served with the
  # plugins' below. Without it the plugin still works, falling back to centre
  # text, so a bad id here must not stop the server.
  case "${HUD_WORKSHOP_ID}" in
    "")
      echo "---Utility Practice: HUD_WORKSHOP_ID unset, HUD falls back to centre text---"
      ;;
    *[!0-9]*)
      echo "---Utility Practice: HUD_WORKSHOP_ID '${HUD_WORKSHOP_ID}' is not a workshop id, HUD falls back to centre text---"
      ;;
    *)
      PRACTICE_HUD_ADDON="${HUD_WORKSHOP_ID}"
      echo "---Utility Practice: HUD addon ${HUD_WORKSHOP_ID}---"
      ;;
  esac
fi

# DEAFCS: a practice server keeps the node's DEAFCS plugins (accessibility:
# DeafCS.DisableValvePremute, Mutesound) but never loads the ones named in
# UTILITY_PRACTICE_SKIP_PLUGINS (skins, by default). Upstream gets the same
# result from its plugin index, which DEAFCS nodes do not have. Only the
# instance's own link is removed, never anything on the node volume: a link
# whose parent is itself a link is left alone.
if [ "${INSTALL_UTILITY_PRACTICE_PLUGIN:-false}" = "true" ]; then
  for skipped_plugin in ${UTILITY_PRACTICE_SKIP_PLUGINS:-}; do
    skipped_link="${INSTANCE_SERVER_DIR}/game/csgo/addons/swiftlys2/plugins/${skipped_plugin}"
    if [ -L "$skipped_link" ] && [ ! -L "$(dirname "$skipped_link")" ]; then
      rm -f "$skipped_link"
      echo "---Utility Practice: not loading ${skipped_plugin} on a practice server---"
    fi
  done
fi

# The practice HUD's addon, and every addon the plugins this server loads list
# in the catalog -- the panel sends those as WORKSHOP_ADDONS. AddonsManager only
# runs when there is something to serve, so a server whose plugins ship no
# addons never makes a player download anything.
WORKSHOP_ADDON_IDS="$(workshop_addon_ids "${PRACTICE_HUD_ADDON:-}" "${WORKSHOP_ADDONS:-}")"
if [ -n "$WORKSHOP_ADDON_IDS" ]; then
  # Bare numeric ids, one per line, so splitting them is safe.
  # shellcheck disable=SC2086
  enable_addons_manager "${INSTANCE_SERVER_DIR}/game/csgo" $WORKSHOP_ADDON_IDS
fi

if [ ! -e "$INSTANCE_SERVER_DIR/game/csgo/addons/swiftlys2/configs/core.jsonc" ]; then
    cp "/opt/server-cfg/core.jsonc" "$INSTANCE_SERVER_DIR/game/csgo/addons/swiftlys2/configs"
fi

if [ "$SHOW_ELO_RANKS" = "true" ]; then
    echo "---Disabling CS2 Server Guidelines (required for Elo Ranks to render)---"
    core_json="$INSTANCE_SERVER_DIR/game/csgo/addons/swiftlys2/configs/core.jsonc"
    sed -i --follow-symlinks 's/"FollowCS2ServerGuidelines"[[:space:]]*:[[:space:]]*true/"FollowCS2ServerGuidelines": false/' "$core_json"
fi

echo "---Check swiftlys2 Install---"
gameinfo_path="${INSTANCE_SERVER_DIR}/game/csgo/gameinfo.gi"
new_line="                        Game    csgo/addons/swiftlys2"

if ! grep -qFx "$new_line" "$gameinfo_path"; then
    echo "---Adding swiftlys2 Loader ---"
    line_number=$(awk '/Game_LowViolence/{print NR; exit}' "$gameinfo_path")
    sed -i "${line_number}a\\$new_line" "$gameinfo_path"
fi

if [ "$STEAM_RELAY" = "true" ]; then
  echo "---Enable Steam Relay---"
  enable_steam_relay "${INSTANCE_SERVER_DIR}/game/csgo/gameinfo_branchspecific.gi"
fi