import copy
import unittest
from disk_discovery import candidate


def disk(name, **kwargs):
    return dict(name=name, type="disk", ro=False, rm=False, fstype=None,
                pttype=None, mountpoints=[None], size=100 * 1024**3, **kwargs)


class DiskDiscoveryTests(unittest.TestCase):
    def setUp(self):
        self.system = disk("/dev/vda", children=[
            dict(name="/dev/vda1", type="part", mountpoints=["/"])])
        self.blank = disk("/dev/vdb")

    def test_select_second_disk(self):
        self.assertEqual(candidate([self.system, self.blank]), self.blank)

    def test_never_select_single_or_ambiguous_disk(self):
        self.assertIsNone(candidate([self.system]))
        self.assertIsNone(candidate([self.system, self.blank, disk("/dev/vdc")]))
        self.assertIsNone(candidate([disk("/dev/vda"), self.blank]))

    def test_reject_used_or_unsafe_disk(self):
        for changes in (dict(fstype="ext4"), dict(pttype="gpt"),
                        dict(ro=True), dict(rm=True), dict(mountpoints=["/data"]),
                        dict(children=[dict(type="part")])):
            with self.subTest(changes=changes):
                d = copy.deepcopy(self.blank)
                d.update(changes)
                self.assertIsNone(candidate([self.system, d]))

    def test_root_on_unpartitioned_disk(self):
        self.system.pop("children")
        self.system["mountpoints"] = ["/"]
        self.assertEqual(candidate([self.system, self.blank]), self.blank)


if __name__ == "__main__":
    unittest.main()
