#!/usr/bin/env python3
"""Local boundary tests. The Swiftly exit-zero case is an explicit upstream xfail."""
import os
from pathlib import Path
import shutil
import signal
import subprocess
import tempfile
import time
import unittest

COLLECTOR = Path(os.environ.get("DEAFCS_CRASH_COLLECTOR",
                              Path(__file__).resolve().parents[1] / "crash-report.sh"))


class CrashReportBoundaries(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="crash-boundaries-")
        self.root = Path(self.temp.name)
        (self.root / "dumps").mkdir()
        (self.root / "instance/game/csgo").mkdir(parents=True)
        (self.root / "instance/game/csgo/steam.inf").write_text("PatchVersion=test-build\n")
        self.env = dict(os.environ, TEST_ROOT=str(self.root), SERVER_ID="test-server",
                        MATCH_ID="test-match", GAME_ID="730", SERVER_TYPE="Ranked",
                        EXTRA_GAME_PARAMS="+sv_password test-launch-password")
        self.processes = []

    def tearDown(self):
        pidfile = self.root / "server.pid"
        if pidfile.exists():
            try:
                os.killpg(int(pidfile.read_text()), signal.SIGKILL)
            except (ProcessLookupError, ValueError):
                pass
        for p in self.processes:
            if p.poll() is None:
                p.kill()
                p.wait()
            if p.stdout:
                p.stdout.close()
            if p.stderr:
                p.stderr.close()
        self.temp.cleanup()

    def start(self, server, extra=""):
        path = self.root / "server"
        path.write_text("#!/bin/bash\necho $$ > \"$TEST_ROOT/server.pid\"\n" + server)
        path.chmod(0o755)
        runner = """
source "$1"
CRASH_REPORTS_DIR="$2/reports"
MINIDUMP_DIR="$2/dumps"
INSTANCE_SERVER_DIR="$2/instance"
CONSOLE_PIPE_DIR="$2"
export MINIDUMP_DIR INSTANCE_SERVER_DIR SWIFTLY_CRASH_DIR
umask 022
""" + extra + """
run_server "$2/server"
"""
        p = subprocess.Popen(["bash", "-c", runner, "_", str(COLLECTOR), str(self.root)],
                             env=self.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                             text=True, start_new_session=True)
        self.processes.append(p)
        return p

    def run_server(self, server, extra=""):
        p = self.start(server, extra)
        out, err = p.communicate(timeout=15)
        return p.returncode, out, err

    def report(self):
        files = list((self.root / "reports").glob("*/*/crash.txt"))
        self.assertEqual(len(files), 1)
        return files[0].parent

    def wait_for_server(self):
        for _ in range(100):
            if (self.root / "server.pid").exists():
                time.sleep(0.1)
                return
            time.sleep(0.02)
        self.fail("fake server did not start")

    @unittest.expectedFailure
    def test_swiftly_only_dump_with_exit_zero_requires_report(self):
        # Genuine regression assertion, kept as XFAIL while crash detection remains upstream 1:1.
        status, _, _ = self.run_server("""
mkdir -p "$INSTANCE_SERVER_DIR/$SWIFTLY_CRASH_DIR/start"
printf fake-dump > "$INSTANCE_SERVER_DIR/$SWIFTLY_CRASH_DIR/start/test.dmp"
exit 0
""")
        self.assertEqual(status, 0)
        self.assertTrue(list((self.root / "reports").glob("*/*/test.dmp")),
                        "upstream misses Swiftly-only dump when exit status is zero")

    def prune(self, root=None, server_dir=None, server_id="test-server"):
        root = root or self.root / "reports"
        server_dir = server_dir or root / server_id
        return subprocess.run(
            ["bash", "-c", 'source "$1"; CRASH_REPORTS_DIR="$2"; SERVER_ID="$4"; '
             'CRASH_REPORTS_KEPT=1; prune_crash_reports "$3"', "_",
             str(COLLECTOR), str(root), str(server_dir), server_id],
            capture_output=True, text=True, timeout=5)

    def test_security_retention_preserves_external_and_unrelated_files(self):
        external = self.root / "external"
        (external / "nested").mkdir(parents=True)
        (external / "nested/keep.dem").write_text("keep")
        server = self.root / "reports/test-server"
        server.mkdir(parents=True)
        (server / "20260901T000000Z").symlink_to(external, target_is_directory=True)
        (server / "unrelated-logs").mkdir()
        (server / "unrelated-logs/keep.log").write_text("keep")
        (server / "keep.dem").write_text("keep")
        for stamp in ["20261001T000000Z", "20261002T000000Z"]:
            (server / stamp).mkdir()
        # An internal symlink in a real old report must not be followed either.
        (server / "20261001T000000Z/link").symlink_to(external, target_is_directory=True)
        self.assertEqual(self.prune().returncode, 0)
        self.assertTrue((external / "nested/keep.dem").is_file(), "external demo was deleted")
        self.assertEqual((external / "nested/keep.dem").read_text(), "keep")
        self.assertEqual((server / "unrelated-logs/keep.log").read_text(), "keep")
        self.assertEqual((server / "keep.dem").read_text(), "keep")
        self.assertTrue((server / "20260901T000000Z").is_symlink())
        self.assertFalse((server / "20261001T000000Z").exists())
        self.assertTrue((server / "20261002T000000Z").is_dir())

    def test_security_retention_rejects_intermediate_symlinks(self):
        for component in ["root", "parent", "server"]:
            with self.subTest(component=component):
                base = self.root / component
                base.mkdir()
                external = base / "external"
                server = external / "test-server"
                for stamp in ["20261001T000000Z", "20261002T000000Z"]:
                    (server / stamp).mkdir(parents=True)
                    (server / stamp / "keep").write_text("keep")
                if component == "root":
                    root = base / "reports"
                    root.symlink_to(external, target_is_directory=True)
                    target = root / "test-server"
                elif component == "parent":
                    (base / "link").symlink_to(base, target_is_directory=True)
                    root = base / "link/external"
                    target = root / "test-server"
                else:
                    root = base / "reports"
                    root.mkdir()
                    target = root / "test-server"
                    target.symlink_to(server, target_is_directory=True)
                self.assertNotEqual(self.prune(root, target).returncode, 0)
                self.assertEqual((server / "20261001T000000Z/keep").read_text(), "keep")

    def test_security_retention_rejects_wrong_namespace_and_server_id(self):
        outside = self.root / "outside"
        for stamp in ["20261001T000000Z", "20261002T000000Z"]:
            (outside / stamp).mkdir(parents=True)
            (outside / stamp / "keep").write_text("keep")
        (self.root / "reports").mkdir()
        for server_id in ["test-server", "../outside"]:
            with self.subTest(server_id=server_id):
                self.assertNotEqual(self.prune(server_dir=outside, server_id=server_id).returncode, 0)
                self.assertEqual((outside / "20261001T000000Z/keep").read_text(), "keep")

    def test_security_collection_rejects_linked_report_files(self):
        clock = """
date() {
  if [ "$#" -eq 2 ] && [ "$2" = "+%s" ]; then printf '1791715000\\n';
  else command date "$@"; fi
}
"""
        report = self.root / "reports/test-server"
        # Derive the exact fixed-clock directory rather than duplicating UTC arithmetic.
        stamp = subprocess.check_output(["date", "-u", "-d", "@1791715000", "+%Y%m%dT%H%M%SZ"], text=True).strip()
        report = report / stamp
        report.mkdir(parents=True)
        outside = self.root / "outside.txt"
        outside.write_text("keep")
        outside.chmod(0o644)
        for kind in ["symlink", "hardlink"]:
            with self.subTest(kind=kind):
                target = report / "crash.txt"
                outside.write_text("keep")
                outside.chmod(0o644)
                if kind == "symlink":
                    target.symlink_to(outside)
                else:
                    os.link(outside, target)
                try:
                    _, out, _ = self.run_server("echo crash\nexit 1\n", clock)
                    self.assertIn("unable to save a crash report", out)
                    self.assertEqual(outside.read_text(), "keep")
                    self.assertEqual(outside.stat().st_mode & 0o777, 0o644)
                finally:
                    target.unlink()

    def test_security_linked_dump_sources_leave_unrelated_files_untouched(self):
        outside = self.root / "outside.txt"
        outside.write_text("keep")
        outside.chmod(0o644)
        os.link(outside, self.root / "dumps/hardlink.dmp")
        (self.root / "dumps/symlink.dmp").symlink_to(outside)
        self.run_server("echo crash\nexit 1\n")
        report = self.report()
        self.assertFalse((report / "hardlink.dmp").exists())
        self.assertFalse((report / "symlink.dmp").exists())
        self.assertEqual(outside.read_text(), "keep")
        self.assertEqual(outside.stat().st_mode & 0o777, 0o644)
        self.assertTrue((self.root / "dumps/hardlink.dmp").is_file())
        self.assertTrue((self.root / "dumps/symlink.dmp").is_symlink())

    def test_security_collection_rejects_symlinked_root(self):
        outside = self.root / "outside"
        outside.mkdir()
        outside.chmod(0o755)
        (self.root / "reports").symlink_to(outside, target_is_directory=True)
        _, out, _ = self.run_server("echo crash\nexit 1\n")
        self.assertIn("unable to save a crash report", out)
        self.assertEqual(list(outside.iterdir()), [])
        self.assertEqual(outside.stat().st_mode & 0o777, 0o755)

    def test_security_all_artifacts_and_moved_dumps_are_private(self):
        unrelated = self.root / "unrelated.dem"
        unrelated.write_text("keep")
        unrelated.chmod(0o644)
        checks = """
mv() {
  [ "$(stat -c %a "$TEST_ROOT/reports")" = 700 ] || exit 91
  [ "$(stat -c %a "$TEST_ROOT/reports/$SERVER_ID")" = 700 ] || exit 92
  for dir in "$TEST_ROOT"/console.*; do
    [ "$(stat -c %a "$dir")" = 700 ] || exit 93
    for file in "$dir"/*; do
      [ -e "$file" ] || continue
      [ "$(stat -c %a "$file")" = 600 ] || exit 94
    done
  done
  command mv "$@"
}
"""
        status, _, _ = self.run_server("""
echo "game_umask=$(umask)"
echo 'Host activate: Loading (de_test)'
printf engine > "$MINIDUMP_DIR/engine.dmp"
chmod 0777 "$MINIDUMP_DIR/engine.dmp"
mkdir -p "$INSTANCE_SERVER_DIR/$SWIFTLY_CRASH_DIR/start"
printf swiftly > "$INSTANCE_SERVER_DIR/$SWIFTLY_CRASH_DIR/start/swiftly.dmp"
printf '{}' > "$INSTANCE_SERVER_DIR/$SWIFTLY_CRASH_DIR/start/crashinfo.json"
chmod 0644 "$INSTANCE_SERVER_DIR/$SWIFTLY_CRASH_DIR/start/"*
exit 1
""", checks)
        self.assertEqual(status, 1)
        report = self.report()
        self.assertEqual({f.name for f in report.iterdir()},
                         {"crash.txt", "console.log", "engine.dmp", "swiftly.dmp", "crashinfo.json"})
        for directory in [report, report.parent, report.parent.parent]:
            self.assertEqual(directory.stat().st_mode & 0o777, 0o700)
        for file in report.iterdir():
            self.assertEqual(file.stat().st_mode & 0o777, 0o600, file.name)
        self.assertIn("game_umask=0022", (report / "console.log").read_text())
        self.assertEqual(unrelated.stat().st_mode & 0o777, 0o644)
        self.assertEqual(unrelated.read_text(), "keep")

    def test_sigterm_wrapper_has_no_collection_guarantee(self):
        p = self.start("echo running\nexec sleep 30\n")
        self.wait_for_server()
        p.send_signal(signal.SIGTERM)
        p.wait(timeout=5)
        os.killpg(int((self.root / "server.pid").read_text()), signal.SIGKILL)
        p.communicate(timeout=5)
        self.assertEqual(p.returncode, -signal.SIGTERM)
        self.assertFalse((self.root / "reports").exists())

    def test_sigkill_wrapper_cannot_collect(self):
        p = self.start("echo running\nexec sleep 30\n")
        self.wait_for_server()
        p.kill()
        p.wait(timeout=5)
        os.killpg(int((self.root / "server.pid").read_text()), signal.SIGKILL)
        p.communicate(timeout=5)
        self.assertEqual(p.returncode, -signal.SIGKILL)
        self.assertFalse((self.root / "reports").exists())

    def test_killed_child_reports_137_but_cannot_prove_oom(self):
        # SIGKILL is the same wait status as child OOM; this does not simulate a cgroup OOM.
        status, _, _ = self.run_server("echo running\nkill -KILL $$\n")
        self.assertEqual(status, 137)
        self.assertIn("exit_status=137", (self.report() / "crash.txt").read_text())


    @unittest.skipUnless(os.environ.get("DEAFCS_TEST_ACTUAL_OOM") == "1",
                         "opt-in test requires a disposable container limited to <=128 MiB")
    def test_actual_child_cgroup_oom_is_collected(self):
        maximum = Path("/sys/fs/cgroup/memory.max").read_text().strip()
        self.assertNotEqual(maximum, "max", "refuse unbounded memory allocation")
        self.assertLessEqual(int(maximum), 128 * 1024 * 1024)
        def oom_kills():
            events = dict(line.split() for line in
                          Path("/sys/fs/cgroup/memory.events").read_text().splitlines())
            return int(events["oom_kill"])
        before = oom_kills()
        status, _, _ = self.run_server("""
payload=x
while :; do payload="$payload$payload"; done
""")
        self.assertGreater(oom_kills(), before, "kernel must confirm an actual cgroup OOM kill")
        self.assertEqual(status, 137)
        self.assertIn("exit_status=137", (self.report() / "crash.txt").read_text())

    def test_security_permissions_and_console_are_private(self):
        status, _, _ = self.run_server("echo test-private-console-marker\nexit 1\n")
        self.assertEqual(status, 1)
        report = self.report()
        self.assertEqual(report.stat().st_mode & 0o777, 0o700)
        self.assertEqual((report / "crash.txt").stat().st_mode & 0o777, 0o600)
        metadata = (report / "crash.txt").read_text()
        self.assertIn("+sv_password <redacted>", metadata)
        self.assertNotIn("test-launch-password", metadata)
        self.assertIn("test-private-console-marker", (report / "console.log").read_text())


    def test_restrictive_inherited_umask_is_respected(self):
        self.run_server("echo crash\nexit 1\n", "umask 077\n")
        report = self.report()
        self.assertEqual(report.stat().st_mode & 0o777, 0o700)
        self.assertEqual((report / "crash.txt").stat().st_mode & 0o777, 0o600)

    def test_same_second_reuses_report_directory(self):
        fixed_clock = """
date() {
  if [ "$#" -eq 2 ] && [ "$1" = "-u" ] && [ "$2" = "+%s" ]; then
    printf '1791715000\\n'
  else
    command date "$@"
  fi
}
"""
        self.run_server("echo first-report\nexit 1\n", fixed_clock)
        self.run_server("echo second-report\nexit 1\n", fixed_clock)
        report = self.report()
        self.assertIn("second-report", (report / "console.log").read_text())
        self.assertNotIn("first-report", (report / "console.log").read_text())

    def test_swiftly_crashinfo_basename_collision(self):
        self.run_server("""
for name in a b; do
  mkdir -p "$INSTANCE_SERVER_DIR/$SWIFTLY_CRASH_DIR/$name"
  printf "$name" > "$INSTANCE_SERVER_DIR/$SWIFTLY_CRASH_DIR/$name/crashinfo.json"
  printf fake-dump > "$INSTANCE_SERVER_DIR/$SWIFTLY_CRASH_DIR/$name/$name.dmp"
done
exit 1
""")
        report = self.report()
        self.assertEqual((report / "crashinfo.json").read_text(), "b")
        self.assertEqual(len(list(report.glob("*.dmp"))), 2)

    def test_unavailable_report_directory_is_reported(self):
        # Deterministic ENOSPC fault injection for report-directory creation.
        blocked = """
mkdir() {
  case "$*" in *"/reports"*) echo 'mkdir: No space left on device' >&2; return 1;; esac
  command mkdir "$@"
}
"""
        status, out, err = self.run_server("echo crash\nexit 1\n", blocked)
        self.assertEqual(status, 1)
        self.assertIn("unable to save a crash report", out)
        self.assertIn("No space left on device", err)
        self.assertFalse((self.root / "reports").exists())

    @unittest.skipUnless(Path("/full").is_dir(), "requires a disposable 1 MiB /full tmpfs")
    def test_real_enospc_move_can_still_log_saved(self):
        small = Path(tempfile.mkdtemp(prefix="crash-full-", dir="/full"))
        try:
            (self.root / "dumps/test.dmp").write_bytes(b"x" * (2 * 1024 * 1024))
            extra = 'CRASH_REPORTS_DIR="' + str(small) + '/reports"\n'
            status, out, err = self.run_server("echo crash\nexit 1\n", extra)
            self.assertEqual(status, 1)
            self.assertIn("No space left on device", err)
            self.assertTrue((self.root / "dumps/test.dmp").exists())
            self.assertIn("crash report saved", out)
        finally:
            shutil.rmtree(small)

    def test_password_redaction_does_not_cover_quoted_spaces(self):
        self.env["EXTRA_GAME_PARAMS"] = '+sv_password "test secret suffix"'
        self.run_server("exit 1\n")
        self.assertIn('secret suffix"', (self.report() / "crash.txt").read_text())


if __name__ == "__main__":
    unittest.main(verbosity=2)
