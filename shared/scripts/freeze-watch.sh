#!/bin/bash

# freeze-watch: samples the CS2 server process's CPU usage every few seconds
# and keeps a short rolling log plus a flagged snapshot on the first sustained
# stall, so a hang that gets the pod replaced (not a clean crash -- see
# write_crash_report in crash-report.sh, which only fires on a nonzero exit
# status or minidump, neither of which a frozen-but-alive process produces)
# still leaves evidence behind. Investigated 2026-10-13: a match server froze
# for ~90s (only tiny keepalive packets, confirmed by the host's own network
# logs -- no packet loss, no filtering), the backend replaced the pod before
# it recovered, and nothing survived to explain why: no crash report (the
# process never exited), no container logs (the pod was already gone by the
# time anything tried to read them).
#
# Written continuously to the demos PV (survives the pod, unlike
# /opt/instance) and overwritten in place, so no matter how the container
# dies -- even SIGKILL, which skips crash-report.sh's own cleanup entirely --
# the last few minutes before death are already on disk.

FREEZE_WATCH_INTERVAL="${FREEZE_WATCH_INTERVAL:-2}"
# 5 samples * 2s interval = 10s of ~0% CPU before a stall is flagged.
FREEZE_WATCH_STALL_SAMPLES="${FREEZE_WATCH_STALL_SAMPLES:-5}"
FREEZE_REPORTS_DIR="/opt/demos/.freeze-reports"
# 150 lines * 2s interval = 5 minutes of history.
FREEZE_LOG_LINES_KEPT="${FREEZE_LOG_LINES_KEPT:-150}"

FREEZE_WATCH_PID=""

start_freeze_watch() {
  local server_pid="$1"

  # Same validation as crash-report.sh's write_crash_report -- SERVER_ID
  # becomes a directory name on the shared demos volume.
  local server_id="${SERVER_ID:-unknown}"
  [[ "$server_id" =~ ^[A-Za-z0-9_-]+$ ]] || server_id="unknown"

  local report_dir="${FREEZE_REPORTS_DIR}/${server_id}"
  (umask 077; mkdir -p "$report_dir") 2>/dev/null || return 0

  local log_file="${report_dir}/rolling.log"
  local flag_file="${report_dir}/last-freeze.txt"
  (umask 077; : > "$log_file") 2>/dev/null

  (
    local nproc_count hz
    nproc_count="$(nproc 2>/dev/null || echo 1)"
    hz="$(getconf CLK_TCK 2>/dev/null || echo 100)"

    local prev_jiffies="" prev_time="" stall_count=0

    while kill -0 "$server_pid" 2>/dev/null; do
      sleep "$FREEZE_WATCH_INTERVAL"

      local stat_file="/proc/${server_pid}/stat"
      [ -r "$stat_file" ] || continue

      # Fields 14/15 (utime/stime) are what we want. The comm field (2) is
      # parenthesized and can itself contain spaces/parens, so split off
      # everything up to the LAST ')' rather than trusting field position.
      local stat_line rest utime stime jiffies now cpu_pct djiffies dtime
      stat_line="$(cat "$stat_file" 2>/dev/null)" || continue
      rest="${stat_line##*) }"
      utime="$(awk '{print $12}' <<< "$rest")"
      stime="$(awk '{print $13}' <<< "$rest")"
      [ -n "$utime" ] && [ -n "$stime" ] || continue

      jiffies=$((utime + stime))
      now="$(date -u +%s)"
      cpu_pct="0.0"

      if [ -n "$prev_jiffies" ] && [ -n "$prev_time" ]; then
        djiffies=$((jiffies - prev_jiffies))
        dtime=$((now - prev_time))
        if [ "$dtime" -gt 0 ]; then
          cpu_pct="$(awk -v dj="$djiffies" -v dt="$dtime" -v hz="$hz" -v np="$nproc_count" \
            'BEGIN { printf "%.1f", (dj / hz / dt) * 100 / np }')"
        fi
        if [ "$djiffies" -le 1 ]; then
          stall_count=$((stall_count + 1))
        else
          stall_count=0
        fi
      fi
      prev_jiffies="$jiffies"
      prev_time="$now"

      local rss threads
      rss="$(awk '/^VmRSS:/ {print $2}' "/proc/${server_pid}/status" 2>/dev/null)"
      threads="$(awk '/^Threads:/ {print $2}' "/proc/${server_pid}/status" 2>/dev/null)"

      printf '%s cpu=%s%% rss=%sKB threads=%s stall_samples=%d\n' \
        "$(date -u '+%F %T%z')" "$cpu_pct" "${rss:-0}" "${threads:-0}" "$stall_count" \
        >> "$log_file" 2>/dev/null

      # Trim in place -- cheap at this size, and keeps the file bounded over
      # a long match instead of growing for the server's whole lifetime.
      tail -n "$FREEZE_LOG_LINES_KEPT" "$log_file" > "${log_file}.tmp" 2>/dev/null &&
        mv -f "${log_file}.tmp" "$log_file" 2>/dev/null

      # Only the first flag of a given stall run -- fires once per freeze,
      # not once per second for its whole duration.
      if [ "$stall_count" -eq "$FREEZE_WATCH_STALL_SAMPLES" ]; then
        {
          echo "=== freeze suspected $(date -u '+%F %T%z'), pid=${server_pid}, ~0% CPU for $((FREEZE_WATCH_STALL_SAMPLES * FREEZE_WATCH_INTERVAL))s"
          echo "--- per-thread CPU (ps -eLo), top 40 ---"
          ps -eLo pid,tid,pcpu,stat,wchan:32,comm --sort=-pcpu 2>/dev/null |
            awk -v p="$server_pid" 'NR==1 || $1==p' | head -40
          echo "--- socket state (game + tv ports) ---"
          ss -tunap 2>/dev/null | grep -E ":${SERVER_PORT:-0}\>|:${TV_PORT:-0}\>"
          echo "--- /proc/${server_pid}/status ---"
          cat "/proc/${server_pid}/status" 2>/dev/null
          echo
        } >> "$flag_file" 2>/dev/null
      fi
    done
  ) &

  FREEZE_WATCH_PID=$!
}

stop_freeze_watch() {
  [ -n "$FREEZE_WATCH_PID" ] || return 0
  kill "$FREEZE_WATCH_PID" 2>/dev/null
  wait "$FREEZE_WATCH_PID" 2>/dev/null
  FREEZE_WATCH_PID=""
}
