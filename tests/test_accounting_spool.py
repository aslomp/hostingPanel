import importlib.util
import pathlib
import tempfile
import unittest
from unittest.mock import patch, MagicMock

path = pathlib.Path(__file__).resolve().parents[1] / "deploy/helm/hostingpanel/files/accounting-spool.py"
spec = importlib.util.spec_from_file_location("spool", path)
spool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(spool)

class SpoolTests(unittest.TestCase):
    def test_seal_and_replay(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            record = spool.encode(b"HP_ACCESS_V1 00000000000000000000000000000001 00000000000000000000000000000002 1795860000 200 1 2 3")
            segment = spool.seal(root, [record])
            self.assertTrue(spool.VALID.fullmatch(segment.name))
            self.assertEqual(segment.read_bytes(), record)
            with patch.object(spool.urllib.request, "build_opener") as opener:
                opener.return_value.open.side_effect = OSError("receiver offline")
                with self.assertRaises(OSError):
                    spool.deliver(segment, "https://panel.invalid", "x"*32)
                self.assertTrue(segment.exists())
                response = MagicMock()
                response.__enter__.return_value.status = 204
                opener.return_value.open.side_effect = None
                opener.return_value.open.return_value = response
                spool.deliver(segment, "https://panel.invalid", "x"*32)
                self.assertFalse(segment.exists())
    def test_system_and_control_lines(self):
        self.assertIsNone(spool.encode(b"HP_SYSTEM_V1 200 1 2 3"))
        with self.assertRaises(ValueError):
            spool.encode(b"HP_ERROR_V1 bad\x00")
if __name__ == "__main__":
    unittest.main()
