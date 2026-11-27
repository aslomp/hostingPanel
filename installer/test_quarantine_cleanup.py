import importlib.util
import os
import pathlib
import tempfile
import time
import unittest

spec = importlib.util.spec_from_file_location("cleanup", pathlib.Path(__file__).with_name("quarantine-cleanup.py"))
cleanup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cleanup)


class QuarantineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.quarantine = self.root / "quarantine"
        self.quarantine.mkdir(mode=0o700)
        self.owner = os.getuid()

    def run_cleanup(self, now, dry=False):
        return cleanup.cleanup(str(self.quarantine), now, dry, self.owner)

    def test_old_mtime_does_not_expire_new_quarantine(self):
        f = self.quarantine / "malware"
        f.write_text("test")
        os.utime(f, (1, 1))
        self.assertEqual(self.run_cleanup(time.time()), 0)
        self.assertTrue(f.exists())

    def test_expiry_and_dry_run(self):
        f = self.quarantine / "malware"
        f.write_text("test")
        boundary = f.stat().st_ctime + cleanup.RETENTION
        self.assertEqual(self.run_cleanup(boundary - 1), 0)
        self.assertEqual(self.run_cleanup(boundary, True), 1)
        self.assertTrue(f.exists())
        self.assertEqual(self.run_cleanup(boundary + 1), 1)
        self.assertFalse(f.exists())

    def test_external_targets_are_not_touched(self):
        outside = self.root / "outside"
        outside.write_text("keep")
        (self.quarantine / "link").symlink_to(outside)
        sub = self.quarantine / "subdirectory"
        sub.mkdir()
        (sub / "file").write_text("keep")
        self.assertEqual(self.run_cleanup(time.time() + cleanup.RETENTION + 1), 0)
        self.assertTrue(outside.exists())
        self.assertTrue((sub / "file").exists())
        self.assertTrue((self.quarantine / "link").is_symlink())

    def test_unsafe_permissions_refused(self):
        self.quarantine.chmod(0o755)
        with self.assertRaises(PermissionError):
            self.run_cleanup(time.time())

    def test_symlink_directory_refused(self):
        self.quarantine.rmdir()
        target = self.root / "target"
        target.mkdir(mode=0o700)
        self.quarantine.symlink_to(target, target_is_directory=True)
        with self.assertRaises(OSError):
            self.run_cleanup(time.time())


if __name__ == "__main__":
    unittest.main()
