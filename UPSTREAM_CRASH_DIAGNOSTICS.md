# Upstream CS2 crash diagnostics, local port

Status: local implementation only, uncommitted. No production image built,
published or deployed. No production access or mutation was performed.

## Baseline and upstream

DEAFCS main and origin/main: 3b5fdb7028ad0809e1bdfb4e5bd7a476692be3f4.
Theft's forced-client-name, clan-tag and Steam Relay fixes are preserved.
SwiftlyS2 remains v1.4.13; pinned Steam runtime and dependency/build fixes are unchanged.

Sources:
- https://github.com/5stackgg/game-server/commit/195f4e750ca6da9b2463b0b159b4384dc6a08d29
- https://github.com/5stackgg/game-server/commit/81137cd9f06bb8245a94646eb59cc68df3882f81
- https://github.com/5stackgg/game-server/commit/365b2f0677df7646354a223f809aad32acf0d822
  (shared crash script and tests only; no hibernation hunks)
- API prerequisite: https://github.com/5stackgg/api/commit/ae113b1e66c48485bf72a24a6489d06d45dc2450

shared/scripts/test/crash-report.test.sh remains a byte-for-byte copy from
365b2f0. The collector starts from that exact source, with only the two authorized
security deviations below. Crash detection, signal handling, metadata fields,
process groups and the Swiftly exit-zero gap remain upstream behavior.

## Integration and deliberate differences

Both production Dockerfiles receive the upstream runtime ARG/ENV RELEASE_VERSION
and GAME_SERVER_IMAGE fields only. Existing release workflows already pass the
build argument and copy shared scripts; no build workflow or dependency changes
are required.

Both launch scripts call upstream run_server with the existing launch arguments.
Swiftly sources crash-report.sh beside its existing util.sh import, preserving
the forced-client-name fetch and its extra launch argument. CounterStrikeSharp
retains its existing 32-bit tickrate logic/comments. Setup scripts, C# plugins,
clan-tag logic, Steam Relay logic, gamedata and configuration are unchanged.

The test workflow adds the upstream scripts job using the existing checkout@v4.
It does not import unrelated action upgrades, missing util.test.sh, extra plugin
projects, or hibernation changes. It also runs DEAFCS's test-only Python boundary
suite. Python is not a runtime image dependency.

The collector retains upstream crash detection and collection. There is no new
storage service, privilege, runtime package, upload endpoint or administrator UI.
The security helpers use existing GNU coreutils realpath/stat, Bash and mkdir,
chmod/mv/rm; these are available in the pinned Steam runtime.

## Authorized security deviations from 365b2f0

1. Safe retention: physical and lexical paths must agree for the configured
   crash-report root and each child, rejecting symlinks in every intermediate
   component and non-normalized paths. SERVER_ID must be a single safe component,
   and the pruning parent must exactly equal root/SERVER_ID. Pruning enters the
   validated physical server directory and deletes only real, non-symlink
   YYYYMMDDTHHMMSSZ directories by relative name, with no trailing slash. It ignores
   normal demos, logs, non-report directories, files and report-directory symlinks.
   Internal links inside a genuine old report are removed as links, not traversed.
   Retention still keeps the newest ten genuine report directories per server.

2. Private reports: console FIFO/log/map artifacts use a mktemp-created 0700
   directory instead of upstream's predictable mktemp -u path. The FIFO and console
   files are 0600. Only the console reader receives umask 077; the game process
   retains the caller's umask. Report root, server and timestamp directories are
   created/secured as 0700; console.log, crash.txt, engine and Swiftly dumps and
   crashinfo.json are secured as 0600. Destinations are private before movement,
   so imported files are inaccessible to other users during chmod. Linked or
   special pre-existing report files are rejected before redirects/chmod. Symlinked
   and hard-linked dump sources are skipped so unrelated files cannot have their
   permissions changed. Only the collector's report namespace is chmodded, and
   directories owned by a different runtime user are rejected, not taken over.

These are deliberate security deviations, not general crash-handling rewrites.
The original collector followed a timestamp symlink during rm -rf and deleted an
external fixture file. It also created world-readable reports under umask 022.
Eight security regression methods (including subcases) detect these defects on
an isolated copy of 365b2f0 and pass on the patched collector. Tests cover root,
parent, server and timestamp symlinks; wrong namespace/IDs; internal links; normal
demos/logs; metadata symlinks/hard links; moved dumps; and temporary artifacts.

The filesystem runtime owner and root remain trusted. Mode 0700 cannot isolate
other root/same-UID containers or defend against a privileged concurrent process
renaming owned directories. Private parent directories plus relative deletion
prevent unprivileged path replacement from escaping the namespace. No shell-only
permission change provides an isolation boundary against a hostile root process.
Existing reports from older versions are hidden by the private root; retained
historical files are not recursively rewritten. New/current report files are 0600.
Admin retrieval must stay private; no public serving of this namespace is added.

## Storage and runtime behavior

Container /opt/demos/.crash-reports maps to existing node-local
/opt/5stack/demos/.crash-reports/<SERVER_ID>/<UTC timestamp>/ storage.

Reports include crash.txt, a 2000-line console tail, engine /tmp/dumps files,
and SwiftlyS2 dumps/crashinfo.json from the instance. The limit is ten report
directories per server ID, not a global size or age limit.

The wrapper uses setsid, stdbuf (except 32-bit CS:GO), mawk, bash and GNU tools.
Local tests also run directly on the exact pinned Steam runtime digest
8cd1bdfc418afd8b019a69d6f5a9305865a5f3bb5554102006fdb8f6853d5f67.
Bash 5.1.4, mawk 1.3.4, setsid 2.36.1, stdbuf/coreutils 8.32 and libstdbuf
are present. Shell-fake preload tests pass; real CS2 preload compatibility and
the final assembled image still require verification. No production image built.

## Test results

- Bash syntax checks: pass.
- Unmodified upstream shell suite: all 13 scenarios pass.
- Additional boundary suite with actual OOM and ENOSPC: 19 tests run,
  18 pass and 1 expected failure, no skips. Eight security methods pass.
- Negative control: all eight security methods fail against original upstream
  in disposable fixtures, including external-file deletion reproduction.
- Actual OOM: a disposable local container capped at 64 MiB records an increased
  cgroup oom_kill counter; the surviving wrapper captures child exit 137.
- Swiftly existing .NET tests: 109 pass.
- CounterStrikeSharp existing .NET tests: 49 pass.
- Original upstream shell-test byte comparison and Git diff checks: pass.
- Workflow executes the full boundary file, including new security methods.
- No changes to Dockerfiles, launch integrations, plugins or workflow in the
  security follow-up; all six original integration/test files remain intact.

Initial local harness failures were corrected without changing runtime code:
fake-server children needed exported instance/dump paths; terminated wrapper
tests needed explicit child-group cleanup to release captured pipes.
The first .NET attempt failed because project-specific OutputPath settings wrote
to the read-only mount. Explicit container-local output overrides resolved this;
both suites subsequently ran and passed.

## Confirmed upstream limitations, intentionally unchanged

- Swiftly-only dump plus exit zero: the desired collection assertion is marked
  unittest.expectedFailure and reproduces the missing report. Runtime detection
  checks nonzero exit or engine dumps, not Swiftly-only dumps.
- SIGUSR1 clean stops and crash/stop races pass upstream tests.
- Killing the wrapper with SIGTERM or SIGKILL prevents report creation in local
  tests. Child SIGKILL and child OOM yield 137, which alone cannot identify OOM.
  Whole-container/cgroup OOM, node loss and actual Kubernetes termination were
  not tested and cannot guarantee collection.
- Only launch parameters are redacted; console output and binary dumps remain
  sensitive. Quoted passwords containing spaces are only partly redacted by
  the upstream expression. Tests use public synthetic markers, never credentials.
- Report-directory failure is logged. A real 1 MiB tmpfs ENOSPC test also proves
  failed dump movement can leave the original dump in the ephemeral container
  while logging that a report was saved. Successful log text is not proof of a
  complete report; metadata/write/move failures are not comprehensively handled.
- Same server ID and UTC second reuse one report directory and overwrite console
  data. Multiple Swiftly crashinfo.json basenames collapse to the last file.
- Ten reports per server and 2000 console lines do not bound total bytes, line
  length, all server IDs, or node-wide disk usage. Report creation can outlast
  termination grace when copying large dumps.
- Persistent reports survive pod/Job deletion and image rollback, but remain on
  the original node. There is no centralized backup, automatic upload or analysis.

The two security issues are patched explicitly above. The remaining limitations
are intentionally unchanged; further fixes should be reviewed separately and
preferably proposed upstream.

## Remaining validation and release requirements

Real CS2/Swiftly crash handlers and real CS2 LD_PRELOAD compatibility,
dedicated/offline servers, Kubernetes eviction/termination, full-container OOM,
node failure, throughput/backpressure and real dump-size performance remain
untested. Shell fakes and .NET unit tests do not establish those integrations.

DEAFCS API f6593e89 already ports upstream MATCH_ID and its assertion, but remains
locally committed and undeployed. Production API currently uses 487b49a5.
MATCH_ID is sufficient for the upstream on-demand report's match correlation once
a separately authorized API release provisions new Jobs. It does not backfill
existing Jobs or identify each changing match on a long-lived dedicated server;
those reports rely on server ID/timestamps unless a separate upstream solution
is introduced. No API/WEB/connector changes were made in this task.

Future authorized game-server builds must preserve the current Dockerfiles/base,
supply a meaningful RELEASE_VERSION, verify compiled/plugin versions and runtime
metadata, and record immutable image digests. No schema/migration is required.
Future order: separately release API f6593e89 first, then verified game-server
images, then check newly provisioned Jobs. No release is authorized here.
Before rollout, review disk alerts and administrator access. Existing disk-alert
coverage cannot be established from local docs; no production monitoring inspected
or changed. Ten reports per ID is not a global byte limit, and ignored unsafe or
non-report entries are deliberately not cleaned up.
Use restricted node/SSH retrieval; never expose reports through public demo URLs.

Rollback requires capturing actual running/cached immutable game-server image
digests per node and the relevant pin/tag configuration before release.
Restore only the authorized game-server image references using that future
verified procedure; do not guess a moving latest tag. If API MATCH_ID is released
as well, keep its separate prior API digest for image-only rollback. Retain report
files and existing demos, and preserve reaper/Redis schedules. Rolling back an
image neither restores overwritten reports nor repairs lost dumps.

No commits or pushes have been made. Review before any further authorization.
