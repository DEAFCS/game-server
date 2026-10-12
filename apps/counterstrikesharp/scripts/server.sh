#!/bin/bash

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${SCRIPT_DIR}/crash-report.sh"

echo "---Prepare Server---"
mkdir -p /root/.steam/sdk64
cp -R "${STEAMCMD_DIR}/linux64/"* "/root/.steam/sdk64/"

echo "---Starting Server...--"
cd ${INSTANCE_SERVER_DIR}

if [ "${GAME_ID}" = "740" ]; then
    # ensure 128 tickrate for 740
    case " ${EXTRA_GAME_PARAMS} " in
        *" -tickrate "*)
            ;; # already set, do nothing
        *)
            EXTRA_GAME_PARAMS="${EXTRA_GAME_PARAMS} -tickrate 128"
            ;;
    esac
    SERVER_BINARY="${INSTANCE_SERVER_DIR}/srcds_linux"
    GAME_ARGS="-game csgo"
else
    SERVER_BINARY="${INSTANCE_SERVER_DIR}/game/bin/linuxsteamrt64/cs2"
    GAME_ARGS=""
fi

# BIND_IP is server.host from the API: a plain node's own public_ip normally,
# but the protected IP for a node behind a reverse-proxy DDoS service (e.g.
# KernelHost's Advanced DDoS Protection) -- replies must carry that address as
# their source, or they leave via the backend's real IP instead, asymmetric
# from what the client connected to, and the client times out after Steam
# auth succeeds. Falls back to 0.0.0.0 (bind all, OS picks the outbound IP)
# if unset, matching the previous hardcoded behavior.
run_server "${SERVER_BINARY}" ${GAME_ARGS} -ip ${BIND_IP:-0.0.0.0} -port ${SERVER_PORT} +tv_port ${TV_PORT} -dedicated -dev -usercon +rcon_password ${RCON_PASSWORD} ${EXTRA_GAME_PARAMS}
