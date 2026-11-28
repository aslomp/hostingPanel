import json
import pathlib
import unittest
import tempfile
from unittest.mock import patch
from defaults import defaults, network_defaults, email_valid, complete
from generate import hostname_valid, validate, generate

class DefaultTests(unittest.TestCase):
    def test_network_is_single_node(self):
        routes = [{"prefsrc":"10.0.0.10","dev":"eth0"}]
        interfaces = [{"ifname":"eth0","addr_info":[{"local":"10.0.0.10","prefixlen":24}]}]
        self.assertEqual(network_defaults(routes, interfaces), ("10.0.0.10","10.0.0.10/32"))

    def test_no_destructive_or_public_defaults(self):
        template = json.loads(pathlib.Path(__file__).with_name("cluster.example.json").read_text())
        c = defaults(template, "10.0.0.10", "10.0.0.10/32", "admin@hosting.test", "--full-server")
        self.assertFalse(c["confirmPoolCreation"])
        self.assertEqual(c["poolDevices"], [])
        self.assertEqual(c["panelHostname"], "")
        self.assertEqual(c["ns1"], "")
        self.assertFalse(c["acmeStaging"])

    def test_complete_prompts_only_missing_fields(self):
        template = json.loads(pathlib.Path(__file__).with_name("cluster.example.json").read_text())
        c = defaults(template, "10.0.0.10", "10.0.0.10/32", "admin@hosting.test", "--full-server")
        with patch("defaults.ask_email") as email, patch("defaults.ask_hostname", side_effect=["ns1.hosting.test", "ns2.hosting.test", "panel.hosting.test"]) as hostname:
            complete(c)
            complete(c)
            email.assert_not_called()
            self.assertEqual(hostname.call_count, 3)
        self.assertEqual(c["ns1"], "ns1.hosting.test")
        self.assertEqual(c["panelHostname"], "panel.hosting.test")
        self.assertEqual(c["ns2"], "ns2.hosting.test")

    def test_duplicate_nameservers_rejected(self):
        template = json.loads(pathlib.Path(__file__).with_name("cluster.example.json").read_text())
        c = defaults(template, "10.0.0.10", "10.0.0.10/32", "admin@hosting.test", "--full-server")
        with patch("defaults.ask_hostname", side_effect=["ns1.hosting.test", "ns1.hosting.test", "panel.hosting.test"]):
            with self.assertRaises(ValueError):
                complete(c)

    def test_accounting_requires_explicit_trusted_origin_and_image(self):
        template = json.loads(pathlib.Path(__file__).with_name("cluster.example.json").read_text())
        c = defaults(template, "10.0.0.10", "10.0.0.10/32", "admin@hosting.test", "--full-server")
        self.assertFalse(c["accountingSpoolEnabled"])
        c["accountingSpoolEnabled"] = True
        with self.assertRaises(ValueError):
            validate(c)
        c["accountingEndpoint"] = "https://panel.hosting.test"
        validate(c)
        c["accountingEndpoint"] = "http://panel.hosting.test"
        with self.assertRaises(ValueError):
            validate(c)

    def test_generated_stalwart_storage(self):
        template = json.loads(pathlib.Path(__file__).with_name("cluster.example.json").read_text())
        c = defaults(template, "10.0.0.10", "10.0.0.10/32", "admin@hosting.test", "--full-server")
        with tempfile.TemporaryDirectory() as directory:
            base = pathlib.Path(directory)
            (base / "csi-key").write_text("fixture-key")
            (base / "iscsi-chap").write_text("0123456789abcdef")
            generate(c, base)
            values = json.loads((base / "hosting-values.json").read_text())
            self.assertEqual(values["stalwart"]["storageClassName"], "zfs-iscsi")
            self.assertNotIn("enabled", values["stalwart"])  # chart owns feature default

    def test_hostname_validation(self):
        self.assertTrue(hostname_valid("ns1.hosting.test"))
        for value in ("localhost", "a..test", "-ns.test", "ns-.test", "10.0.0.1", "https://ns.test"):
            self.assertFalse(hostname_valid(value))

    def test_email_validation(self):
        self.assertTrue(email_valid("admin@hosting.test"))
        self.assertFalse(email_valid("admin@example.net"))
        self.assertFalse(email_valid("invalid"))

if __name__ == "__main__":
    unittest.main()
