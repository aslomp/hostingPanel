import datetime as dt
import importlib.util
import pathlib
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("retention", pathlib.Path(__file__).with_name("zfs-retention.py"))
retention = importlib.util.module_from_spec(spec)
spec.loader.exec_module(retention)
UTC = dt.timezone.utc
ROOT = "tank/hosting"


def entry(tier, time, dataset=ROOT):
    return (dataset + "@hp-retention-" + tier + "-" + time.strftime("%Y%m%dT%H%M%SZ"), int(time.timestamp()), tier)


class RetentionTests(unittest.TestCase):
    def test_hourly(self):
        now = dt.datetime(2026, 11, 27, 18, 30, tzinfo=UTC)
        old = entry("hourly", now - dt.timedelta(hours=72))
        keep = entry("hourly", now - dt.timedelta(hours=71))
        self.assertEqual(list(retention.expired([old, keep], now)), [old[0]])

    def test_daily(self):
        now = dt.datetime(2026, 11, 27, 18, 30, tzinfo=UTC)
        old = entry("daily", now - dt.timedelta(days=7))
        keep = entry("daily", now - dt.timedelta(days=6))
        self.assertEqual(list(retention.expired([old, keep], now)), [old[0]])

    def test_month_can_include_five_sundays(self):
        now = dt.datetime(2026, 11, 29, 1, tzinfo=UTC)
        sundays = [entry("weekly", now - dt.timedelta(weeks=i)) for i in range(6)]
        self.assertEqual(list(retention.expired(sundays, now)), [sundays[-1][0]])

    def test_month_boundary_february(self):
        now = dt.datetime(2026, 3, 31, tzinfo=UTC)
        old = entry("weekly", dt.datetime(2026, 2, 22, tzinfo=UTC))
        keep = entry("weekly", dt.datetime(2026, 3, 1, tzinfo=UTC))
        self.assertEqual(list(retention.expired([old, keep], now)), [old[0]])

    def test_due(self):
        now = dt.datetime(2026, 11, 29, 1, tzinfo=UTC)
        self.assertTrue(retention.due("weekly", now, [], ROOT))
        self.assertFalse(retention.due("weekly", now + dt.timedelta(days=1), [], ROOT))
        self.assertFalse(retention.due("hourly", now, [entry("hourly", now)], ROOT))
        self.assertFalse(retention.due("daily", now, [entry("daily", now)], ROOT))

    def test_inventory_excludes_unowned(self):
        name, created, _ = entry("hourly", dt.datetime(2026, 11, 1, tzinfo=UTC))
        rows = [f"{name}\t{created}\tv1",
                f"{name.replace('tank/hosting', 'tank/other')}\t{created}\tv1",
                f"{name.replace('tank/hosting', 'tank/hosting/home')}\t{created}\t-",
                f"{ROOT}@manual\t{created}\tv1"]
        with patch.object(retention, "zfs", return_value="\n".join(rows)):
            self.assertEqual(retention.inventory(ROOT), [(name, created, "hourly")])


if __name__ == "__main__":
    unittest.main()
