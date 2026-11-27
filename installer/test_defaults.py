import json
import pathlib
import unittest
from defaults import defaults, network_defaults, email_valid

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

    def test_email_validation(self):
        self.assertTrue(email_valid("admin@hosting.test"))
        self.assertFalse(email_valid("admin@example.net"))
        self.assertFalse(email_valid("invalid"))

if __name__ == "__main__":
    unittest.main()
