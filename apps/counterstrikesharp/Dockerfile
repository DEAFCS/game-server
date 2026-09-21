# Pinned to today's "latest-container-runtime-depot" digest -- that tag can move
# under us with no warning (see the broken Debian apt mirror this pin was
# added alongside), so build reproducibility depends on pinning here rather
# than trusting the tag to stay put. Bump deliberately if Valve publishes an
# update worth taking; check via: docker buildx imagetools inspect
# registry.gitlab.steamos.cloud/steamrt/sniper/platform:latest-container-runtime-depot
FROM registry.gitlab.steamos.cloud/steamrt/sniper/platform@sha256:8cd1bdfc418afd8b019a69d6f5a9305865a5f3bb5554102006fdb8f6853d5f67 AS build

# Install .NET SDK 10.0
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    wget \
    ca-certificates \
    && wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh \
    && chmod +x dotnet-install.sh \
    && ./dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && rm -f dotnet-install.sh \
    && rm -rf /var/lib/apt/lists/*

ENV PATH="/usr/share/dotnet:${PATH}"
ENV DOTNET_ROOT="/usr/share/dotnet"

WORKDIR /mod

COPY Directory.Build.props ./
COPY apps/counterstrikesharp/src/FiveStack.csproj apps/counterstrikesharp/src/

RUN dotnet restore apps/counterstrikesharp/src/FiveStack.csproj

COPY shared shared
COPY apps/counterstrikesharp apps/counterstrikesharp

ARG RELEASE_VERSION
ENV RELEASE_VERSION=${RELEASE_VERSION}

RUN sed -i "s/__RELEASE_VERSION__/${RELEASE_VERSION}/" apps/counterstrikesharp/src/FiveStackPlugin.cs

RUN dotnet build -c Release apps/counterstrikesharp/src/FiveStack.csproj -o release

RUN rm /mod/release/CounterStrikeSharp.API.dll

COPY apps/counterstrikesharp/src/lang /mod/release/lang

# New stage for creating the zip file
FROM debian:bookworm-slim AS zip-creator

WORKDIR /zip-content

COPY --from=build /mod/release ./addons/counterstrikesharp/plugins/FiveStack/./

RUN apt-get update && \
    apt-get install -y --no-install-recommends zip && \
    zip -r /mod-release.zip . && \
    rm -rf /var/lib/apt/lists/*

# Pinned to today's "latest-container-runtime-depot" digest -- that tag can move
# under us with no warning (see the broken Debian apt mirror this pin was
# added alongside), so build reproducibility depends on pinning here rather
# than trusting the tag to stay put. Bump deliberately if Valve publishes an
# update worth taking; check via: docker buildx imagetools inspect
# registry.gitlab.steamos.cloud/steamrt/sniper/platform:latest-container-runtime-depot
FROM registry.gitlab.steamos.cloud/steamrt/sniper/platform@sha256:8cd1bdfc418afd8b019a69d6f5a9305865a5f3bb5554102006fdb8f6853d5f67

ENV DATA_DIR="/serverdata"
ENV STEAMCMD_DIR="${DATA_DIR}/steamcmd"
ENV BASE_SERVER_DIR="${DATA_DIR}/serverfiles"
ENV INSTANCE_SERVER_DIR="/opt/instance"

ENV LD_LIBRARY_PATH="/opt/instance/game/bin/linuxsteamrt64:/opt/instance/bin:"

ENV AUTOLOAD_PLUGINS=true
ENV PLUGINS_DIR="/opt/custom-plugins"

ENV INSTALL_5STACK_PLUGIN=true

ENV GAME_ID="730"
ENV GAME_PARAMS=""
ENV GAME_PORT=27015
ENV VALIDATE=false
ENV USER=steam

ENV STEAM_USER="anonymous"
ENV STEAM_PASSWORD=""

ENV SERVER_ID=""
ENV DEFAULT_MAP="de_inferno"

ENV STEAM_RELAY="false"

ENV SERVER_TYPE="Ranked"

ENV METAMOD_URL=https://github.com/alliedmodders/metamod-source/releases/download/2.0.0.1407/mmsource-2.0.0-git1407-linux.tar.gz
ENV COUNTER_STRIKE_SHARP_URL=https://github.com/roflmuffin/CounterStrikeSharp/releases/download/v1.0.371/counterstrikesharp-with-runtime-linux-1.0.371.zip

ENV METAMOD_CSGO_URL=https://mms.alliedmods.net/mmsdrop/1.12/mmsource-1.12.0-git1219-linux.tar.gz
ENV SOURCEMOD_CSGO_URL=https://sm.alliedmods.net/smdrop/1.13/sourcemod-1.13.0-git7297-linux.tar.gz
ENV NO_LOBBY_RESERVATION_URL=https://github.com/nuxencs/NoLobbyReservation/releases/download/v0.0.1/NoLobbyReservation.zip

# The base image's stock sources.list still points at deb.debian.org,
# which 404s now that bullseye is EOL and moved to the archive -- and
# archive.debian.org never carried a separate bullseye-security suite at
# all (its Release file 404s too), so that line has to go rather than
# just being repointed.
#
# libicu-dev was dropped: it's compile-time ICU headers, this is the
# runtime stage, and libicu67 (the actual runtime lib .NET needs) is
# already present in the base image.
#
# lib32gcc-s1/lib32stdc++6/lib32z1 each hard-depend on the *package
# literally named* libc6-i386 -- a separate, older-style i386-compat
# package predating multiarch, distinct from (and not satisfied by) the
# modern libc6:i386 multiarch package this base image already has
# installed and current. archive.debian.org only ever has libc6-i386
# itself at its last-ever build (deb11u11), permanently mismatched
# against the newer libc6 everything else now resolves to, so apt's own
# resolver refuses the whole transaction outright ("held broken
# packages") before dpkg (and any Dpkg::Options) ever gets a say. The
# actual 32-bit libc capability is already satisfied (verified live:
# libc6:i386 present, current), so these three are fetched with
# `apt-get download` (which -- unlike install -- never resolves or
# checks dependencies, just grabs the requested .debs) and installed
# directly via dpkg, whose --force-depends downgrades that one stale
# by-name check to a warning instead of a hard failure.
RUN sed -i \
	-e 's|deb.debian.org|archive.debian.org|g' \
	-e '/-security/d' \
	/etc/apt/sources.list && \
	apt-get update && \
	apt-get install -y --no-install-recommends \
	wget locales procps jq ca-certificates curl unzip rsync lsof && \
	cd /tmp && \
	apt-get download lib32gcc-s1 lib32stdc++6 lib32z1 && \
	dpkg -i --force-depends ./*.deb && \
	rm -f ./*.deb && \
	cd / && \
	echo "en_US.UTF-8 UTF-8" > /etc/locale.gen && \
	locale-gen && \
	rm -rf /var/lib/apt/lists/*

ENV LANG=en_US.UTF-8
ENV LANGUAGE=en_US:en
ENV LC_ALL=en_US.UTF-8

RUN mkdir -p $DATA_DIR $STEAMCMD_DIR $BASE_SERVER_DIR $INSTANCE_SERVER_DIR && \
	useradd -d $DATA_DIR -s /bin/bash $USER && \
	ulimit -n 2048

RUN mkdir -p /opt/metamod /opt/counterstrikesharp && \
	wget -q $METAMOD_URL -O /tmp/metamod.tar.gz && \
	tar -xz -C /opt/metamod -f /tmp/metamod.tar.gz && \
	rm /tmp/metamod.tar.gz && \
	wget -q $COUNTER_STRIKE_SHARP_URL -O /tmp/counterstrikesharp.zip && \
	unzip -q /tmp/counterstrikesharp.zip -d /opt/counterstrikesharp && \
	rm /tmp/counterstrikesharp.zip


RUN mkdir -p /opt/csgo-metamod /opt/csgo-sourcemod /opt/csgo-no-lobby-reservation && \
	wget -q $METAMOD_CSGO_URL -O /tmp/metamod-csgo.tar.gz && \
	tar -xz -C /opt/csgo-metamod -f /tmp/metamod-csgo.tar.gz && \
	rm /tmp/metamod-csgo.tar.gz && \
	wget -q $SOURCEMOD_CSGO_URL -O /tmp/sourcemod-csgo.tar.gz && \
	tar -xz -C /opt/csgo-sourcemod -f /tmp/sourcemod-csgo.tar.gz && \
	rm /tmp/sourcemod-csgo.tar.gz && \
	wget -q $NO_LOBBY_RESERVATION_URL -O /tmp/no-lobby-reservation.zip && \
	unzip -q /tmp/no-lobby-reservation.zip -d /opt/csgo-no-lobby-reservation && \
	rm /tmp/no-lobby-reservation.zip

COPY shared/cfg /opt/server-cfg
COPY apps/counterstrikesharp/cfg /opt/server-cfg
COPY shared/scripts /opt/scripts
COPY apps/counterstrikesharp/scripts /opt/scripts
COPY --from=build /mod/release /opt/mod

RUN mv /opt/metamod/addons /opt/addons && \
	cp -R /opt/counterstrikesharp/addons/metamod /opt/addons && \
	cp -R /opt/counterstrikesharp/addons/counterstrikesharp /opt/addons && \
	mkdir -p /opt/addons/counterstrikesharp/plugins && \
	rm -rf /opt/metamod /opt/counterstrikesharp

ENTRYPOINT ["/bin/bash", "-c", "/opt/scripts/setup.sh && /opt/scripts/server.sh"]