import importlib.util
import pathlib
import unittest

spec = importlib.util.spec_from_file_location("bootstrap_dns", pathlib.Path(__file__).with_name("bootstrap-dns.py"))
dns = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dns)


class FakeApi:
    def __init__(self):
        self.zone = False
        self.rows = []
        self.calls = []

    def exists(self, zone):
        return self.zone

    def records(self, zone):
        return list(self.rows)

    def mutate(self, **fields):
        self.calls.append(fields)
        if fields["action"] == "create":
            self.zone = True
            self.rows = [{"name": "@", "type": "A", "value": fields["ip"]}]
            self.rows += [{"name": "@", "type": "NS", "value": fields[k] + "."} for k in ("ns1", "ns2")]
        else:
            self.rows.append({k: fields[k] for k in ("name", "type", "value")})


class BootstrapTests(unittest.TestCase):
    def setUp(self):
        self.config = dict(panelHostname="panel.testhost.xyz", ns1="ns1.testhost.xyz", ns2="ns2.testhost.xyz", webIp="192.0.2.1")

    def test_roundtrip_and_retry(self):
        api = FakeApi()
        dns.bootstrap(api, self.config)
        self.assertEqual([x["action"] for x in api.calls], ["create", "add", "add", "add"])
        self.assertEqual(len(api.rows), 6)
        dns.bootstrap(api, self.config)
        self.assertEqual(len(api.calls), 4)

    def test_conflict_does_not_mutate(self):
        api = FakeApi()
        dns.bootstrap(api, self.config)
        api.rows.append({"name": "panel", "type": "A", "value": "192.0.2.2"})
        with self.assertRaises(RuntimeError):
            dns.bootstrap(api, self.config)
        self.assertEqual(len(api.calls), 4)

    def test_external_nameserver_requires_explicit_zone(self):
        self.config["ns2"] = "ns2.external.test"
        with self.assertRaises(ValueError):
            dns.plan(self.config)
        self.config["panelDnsZone"] = "testhost.xyz"
        zone, ns, addresses = dns.plan(self.config)
        self.assertEqual(zone, "testhost.xyz")
        self.assertNotIn(("ns2.external.test", "192.0.2.1"), addresses)
