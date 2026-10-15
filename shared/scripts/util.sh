#!/bin/bash

create_directories() {
  local base_dir="$1"
  shift

  for dir in "$@"; do
    mkdir -p "$base_dir/$dir"
  done
}

create_symlinks() {
  local source_path="$1"
  local destination_path="$2"

  for file in "$source_path"/*; do
    local relative_path="${file#$source_path/}"
    local destination_file="$destination_path/$relative_path"

    if [ -f "$file" ]; then
      if [ ! -e "$destination_file" ]; then
        ln -s "$file" "$destination_file"
      fi
    elif [ -d "$file" ]; then
      if [ ! -e "$destination_file" ]; then
        ln -s "$file" "$destination_file"
      fi
      create_symlinks "$file" "$destination_file"
    fi
  done
}

# Valve keeps the server's SteamAppId 730 in this file, so the relay settings are
# added to it rather than replacing it. The instance copy is a symlink onto the
# node's game files, so the result is moved over the link, never written through.
enable_steam_relay() {
  local file="$1"

  awk '
    convars && /{/ { print; print "\t\t\"net_p2p_listen_dedicated\" \"1\""; convars = 0; next }
    /^[[:space:]]*ConVars[[:space:]]*$/ { convars = 1; seen = 1 }
    /^}/ {
      if (!seen) { print "\tConVars\n\t{\n\t\t\"net_p2p_listen_dedicated\" \"1\"\n\t}" }
      print "\tNetworkSystem\n\t{\n\t\t\"CreateListenSocketP2P\" \"2\"\n\t}"
    }
    { print }
  ' "$file" > "$file.relay"

  mv -f "$file.relay" "$file"
}


# Downloads the registered players' DEAFCS names as a KeyValues file for
# +sv_load_forced_client_names_file. Must finish before CS2 starts, because
# the server only reads the file at launch. The file is written to a temp
# path, checked, and then moved into place, so CS2 never sees a partial file.
# Returns non-zero (and leaves nothing behind) on any failure; callers start
# the server without the parameter in that case.
fetch_forced_client_names() {
  local target="$1"
  local base="${API_DOMAIN%/}"
  local tmp="${target}.tmp"
  local attempt

  # The pod's API_DOMAIN already carries its scheme (https://api...); accept a bare host too.
  case "$base" in
    http://*|https://*) ;;
    *) base="https://${base}" ;;
  esac
  local url="${base}/matches/forced-client-names/${SERVER_ID}"

  if [ -z "${API_DOMAIN}" ] || [ -z "${SERVER_ID}" ] || [ -z "${SERVER_API_PASSWORD}" ]; then
    echo "forced names: API_DOMAIN, SERVER_ID or SERVER_API_PASSWORD missing"
    return 1
  fi

  rm -f "$target" "$tmp"

  for attempt in 1 2 3; do
    if curl -fsS --max-time 8 -H "Authorization: Bearer ${SERVER_API_PASSWORD}" -o "$tmp" "$url" \
      && head -n 1 "$tmp" | grep -q '^"Names"' \
      && tail -n 1 "$tmp" | grep -q '^}'; then
      mv -f "$tmp" "$target"
      echo "forced names: ready ($(grep -c '^[[:space:]]*"[0-9]' "$target") players)"
      return 0
    fi

    echo "forced names: attempt ${attempt} failed"
    rm -f "$tmp"
    sleep 2
  done

  echo "forced names: FAILED, starting without forced names"
  return 1
}

# --- Upstream 5Stack (7658cc8): practice HUD workshop addon helpers ---

# A directory reached through custom-plugins is a symlink onto the node-wide
# volume, so writing under it leaks into every other server on the node and
# survives this match. Swap it for a real directory seeded from the same
# contents before writing.
materialize_dir() {
  local dir="$1"

  if [ ! -L "$dir" ]; then
    mkdir -p "$dir"
    return 0
  fi

  local target
  target="$(readlink -f "$dir")"

  rm "$dir"
  mkdir -p "$dir"

  if [ -d "$target" ]; then
    cp -R "$target/." "$dir/"
  fi
}

# A dedicated server mounts the node-wide volume at /opt/node-plugins and its
# own directory at /opt/custom-plugins, so a link into the latter is that one
# server's disk. Writing through it keeps the file on the server's Files tab,
# and keeps whatever plugins write at runtime there too; copying it out left
# both inside the container, gone on the next restart.
server_owned_link() {
  local dir="$1"

  if [ ! -L "$dir" ] || [ ! -d "${NODE_PLUGINS_DIR:-/opt/node-plugins}" ]; then
    return 1
  fi

  local own
  own="$(readlink -f "${CUSTOM_PLUGINS_DIR:-/opt/custom-plugins}")"

  if [ -z "$own" ]; then
    return 1
  fi

  case "$(readlink -f "$dir")/" in
    "$own/"*) return 0 ;;
  esac

  return 1
}

materialize_for_write() {
  local root="$1"
  local relative="$2"
  local current="$root"
  local directory
  directory="$(dirname "$relative")"

  if [ "$directory" != "." ]; then
    local remaining="$directory"

    while [ -n "$remaining" ]; do
      local segment="${remaining%%/*}"

      current="$current/$segment"

      if ! server_owned_link "$current"; then
        materialize_dir "$current"
      fi

      if [ "$remaining" = "$segment" ]; then
        remaining=""
      else
        remaining="${remaining#*/}"
      fi
    done
  fi

  printf '%s/%s\n' "$current" "$(basename "$relative")"
}

# CS2 has no server-to-client file transfer, so anything a plugin shows or plays
# that the game does not ship -- models, sounds, Panorama layouts -- reaches
# players as a workshop addon, which AddonsManager names to each connecting
# client for Steam to deliver.
#
# Prints each workshop id in its arguments once, in order, one per line. An
# argument may be a comma-separated list. AddonsManager reads its config with
# optional:false and validates it on start, so one entry that is not a bare
# numeric id -- a pasted quote, a URL, a trailing comma -- takes it down along
# with every addon it serves. Those are dropped with a warning instead.
workshop_addon_ids() {
  local seen="," list id
  local -a ids

  for list in "$@"; do
    if [ -z "$list" ]; then
      continue
    fi

    IFS="," read -r -a ids <<< "$list"

    for id in "${ids[@]}"; do
      case "$id" in
        "")
          continue
          ;;
        *[!0-9]*)
          echo "---Workshop Addons: '${id}' is not a workshop id, skipping it---" >&2
          continue
          ;;
      esac

      case "$seen" in
        *",$id,"*) continue ;;
      esac

      seen="${seen}${id},"
      printf '%s\n' "$id"
    done
  done
}

# Links the AddonsManager the SwiftlyS2 image ships into this server and has it
# serve exactly these workshop ids, which must already be bare numbers.
#
# The config is rewritten every boot: the ids belong to this server, and a
# stale copy from an older boot would silently serve the wrong addons.
#
# RedownloadAddonOnMount because addons change -- unlike a map, which is
# published once. AddonsManager only checks that an item is installed, not that
# it is current, so without it a server that cached an older copy serves it
# forever and never picks up a republish.
#
# Through materialize_for_write because configs is a symlink onto the node-wide
# volume: written straight through it, one server's addons land in front of
# every other server on the node -- and AddonsManager watches the file, so a
# running server swaps to them live.
enable_addons_manager() {
  local root="$1"
  shift

  local source="${ADDONS_MANAGER_DIR:-/opt/addons-manager/AddonsManager}"

  if [ ! -d "$source" ]; then
    echo "---Workshop Addons: this image has no AddonsManager, players will not get $*---" >&2
    return 0
  fi

  local plugin_dir="$root/addons/swiftlys2/plugins/AddonsManager"

  if [ ! -e "$plugin_dir" ]; then
    mkdir -p "$(dirname "$plugin_dir")"
    ln -s "$source" "$plugin_dir"
  fi

  local config
  config="$(materialize_for_write "$root" "addons/swiftlys2/configs/plugins/AddonsManager/config.jsonc")"

  # A link here is a copy shared with other servers; replace it, never write
  # through it.
  if [ -L "$config" ]; then
    rm -f "$config"
  fi

  if ! jq -n '{Main: {Addons: $ARGS.positional, RedownloadAddonOnMount: true}}' \
    --args "$@" > "$config"; then
    echo "---Workshop Addons: could not write ${config}---" >&2
    return 0
  fi

  echo "---Workshop Addons: serving $* via AddonsManager---"
}
