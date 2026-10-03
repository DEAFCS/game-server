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
  local url="https://${API_DOMAIN}/matches/forced-client-names/${SERVER_ID}"
  local tmp="${target}.tmp"
  local attempt

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
