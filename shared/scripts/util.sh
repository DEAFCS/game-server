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

