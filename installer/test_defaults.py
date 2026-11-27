import json
import pathlib
import unittest
from unittest.mock import patch
from defaults import defaults, network_defaults, email_valid, complete
from generate import hostname_valid

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
        with patch("defaults.ask_email") as email, patch("defaults.ask_hostname", side_effect=["ns1.hosting.test", "panel.hosting.test"]) as hostname:
            complete(c)
            complete(c)
            email.assert_not_called()
            self.assertEqual(hostname.call_count, 2)
        self.assertEqual(c["ns1"], "ns1.hosting.test")
        self.assertEqual(c["panelHostname"], "panel.hosting.test")
        self.assertEqual(c["ns2"], "")

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
