#!/usr/bin/env python3
"""Bootstrap only explicitly configured panel DNS; never write to PowerDNS directly."""
import base64
import ipaddress
import json
import pathlib
import re
import subprocess
import sys
import tempfile
import time
import urllib.parse
from generate import hostname_valid


def plan(config):
    panel = config["panelHostname"]
    if not panel:
        return None
    ns = [config["ns1"], config["ns2"]]
    if not all(hostname_valid(x) for x in [panel, *ns]) or len(set([panel, *ns])) != 3:
        raise ValueError("Distinct panel and nameserver hostnames required")
    # Only infer the immediate parent when all three names share it.
    # Otherwise require explicit authorization of a zone; never guess a public suffix.
    parent = panel.partition(".")[2]
    zone = config.get("panelDnsZone") or parent
    if not hostname_valid(zone) or not panel.endswith("." + zone):
        raise ValueError("panelDnsZone must be a DNS suffix of panelHostname")
    if not config.get("panelDnsZone") and any(x.partition(".")[2] != parent for x in ns):
        raise ValueError("Set panelDnsZone explicitly when nameservers have different parents")
    ip = str(ipaddress.IPv4Address(config["webIp"]))
    addresses = [(panel, ip)]
    addresses += [(x, ip) for x in ns if x.endswith("." + zone)]
    return zone, ns, addresses


def kubectl(*args):
    return subprocess.check_output(["k3s", "kubectl", *args], text=True, timeout=30)


class Api:
    def __init__(self, config, directory):
        self.hostname = config["panelHostname"]
        self.address = str(ipaddress.IPv4Address(config["clusterHost"]))
        secret = json.loads(kubectl("-n", "hosting", "get", "secret", "hostingpanel-bootstrap", "-o", "json"))["data"]
        # Credential material goes through curl stdin, not command line or logs.
        self.auth = base64.b64encode(b":".join(base64.b64decode(secret[k]) for k in ("username", "password"))).decode("ascii")
        cert = pathlib.Path(directory) / "cert.pem"
        cert.write_bytes(base64.b64decode(json.loads(kubectl("-n", "envoy-gateway-system", "get", "secret", "hosting-panel-tls", "-o", "json"))["data"]["tls.crt"]))
        subprocess.run(["openssl", "verify", "-partial_chain", "-trusted", str(cert), "-verify_hostname", self.hostname, str(cert)], check=True, stdout=subprocess.DEVNULL)
        self.key = pathlib.Path(directory) / "pubkey.pem"
        self.key.write_bytes(subprocess.check_output(["openssl", "x509", "-in", str(cert), "-pubkey", "-noout"]))

    def request(self, path, fields=None):
        args = ["curl", "--silent", "--show-error", "--fail", "--connect-timeout", "10",
                "--max-time", "30", "--noproxy", "*", "--resolve", f"{self.hostname}:443:{self.address}",
                "--insecure", "--pinnedpubkey", str(self.key), "--config", "-"]
        if fields is not None:
            args += ["--data", urllib.parse.urlencode(fields)]
        args += ["https://" + self.hostname + path]
        return subprocess.check_output(args, input=f'header = "Authorization: Basic {self.auth}"\n', text=True, timeout=35)

    def exists(self, zone):
        response = self.request("/CMD_API_DNS_ADMIN?" + urllib.parse.urlencode({"action": "exists", "domain": zone}))
        result = urllib.parse.parse_qs(response, strict_parsing=True)
        if result == {"exists": ["1"]}:
            return True
        if result == {"exists": ["0"]}:
            return False
        raise RuntimeError("Legacy DNS existence request failed")

    def mutate(self, **fields):
        result = urllib.parse.parse_qs(self.request("/CMD_API_DNS_ADMIN", fields), keep_blank_values=True, strict_parsing=True)
        if result.get("error") != ["0"]:
            raise RuntimeError("Legacy DNS mutation rejected: " + fields["action"])

    def records(self, zone):
        # Existing typed read endpoint is used solely to avoid duplicate desired records
        # while asynchronous publication is pending; all mutations use the official API.
        result = json.loads(self.request("/hosting/domains/" + urllib.parse.quote(zone, safe="") + "/dns"))
        if result.get("domain") != zone or not isinstance(result.get("records"), list):
            raise RuntimeError("Invalid DNS record inventory")
        return result["records"]


def owner(name, zone):
    if name == "@":
        return zone
    return name.rstrip(".").lower() if name.endswith(".") else name.lower() + "." + zone


def missing_records(records, zone, nameservers, addresses):
    expected = [(zone, "NS", x + ".") for x in nameservers] + [(n, "A", v) for n, v in addresses]
    missing = []
    for name, kind, value in expected:
        at_name = [r for r in records if owner(r["name"], zone) == name]
        same = [r["value"] for r in at_name if r["type"] == kind]
        if kind == "NS":
            if any(v.rstrip(".").lower() not in nameservers for v in same):
                raise RuntimeError("Existing zone has conflicting nameservers")
            present = value.rstrip(".").lower() in [v.rstrip(".").lower() for v in same]
        else:
            if any(r["type"] == "CNAME" for r in at_name) or any(v != value for v in same):
                raise RuntimeError("Existing DNS record conflicts with panel bootstrap: " + name)
            present = value in same
        if not present:
            missing.append((name + ".", kind, value))
    return missing


def bootstrap(api, config):
    zone, ns, addresses = plan(config)
    if not api.exists(zone):
        api.mutate(action="create", domain=zone, ip=config["webIp"], ns1=ns[0], ns2=ns[1])
    for name, kind, value in missing_records(api.records(zone), zone, ns, addresses):
        api.mutate(action="add", domain=zone, name=name, type=kind, value=value)
    if missing_records(api.records(zone), zone, ns, addresses):
        raise RuntimeError("DNS desired-state read-back failed")
    return zone, ns, addresses


def authoritative(server, name, kind, expected, tcp):
    args = ["dig", "@" + server, name + ".", kind, "+norecurse", "+time=2", "+tries=1",
            "+noall", "+comments", "+answer", "+tcp" if tcp else "+notcp", "+ignore"]
    result = subprocess.run(args, text=True, capture_output=True, timeout=5)
    flags = re.search(r"^;; flags: ([^;]+);", result.stdout, re.M)
    if result.returncode or not flags or "aa" not in flags.group(1).split() or "tc" in flags.group(1).split() or "status: NOERROR," not in result.stdout:
        return False
    values = set()
    for line in result.stdout.splitlines():
        parts = line.split()
        if len(parts) == 5 and parts[0].lower() == name.lower() + "." and parts[2:4] == ["IN", kind]:
            values.add(parts[4].lower().rstrip("."))
    return values == set(x.lower().rstrip(".") for x in expected)


def verify(config, zone, ns, addresses):
    deadline = time.monotonic() + 120
    queries = [(zone, "NS", ns)] + [(n, "A", [v]) for n, v in addresses]
    while time.monotonic() < deadline:
        if all(authoritative(config["clusterHost"], n, kind, values, tcp)
               for tcp in (False, True) for n, kind, values in queries):
            print("Panel DNS round trip passed: authoritative UDP and TCP")
            return
        time.sleep(3)
    raise RuntimeError("Authoritative DNS round trip failed; inspect DNS reconciliation and service exposure")


def main():
    config = json.loads(pathlib.Path(sys.argv[1]).read_text())
    if plan(config) is None:
        return
    with tempfile.TemporaryDirectory() as directory:
        api = Api(config, directory)
        zone, ns, addresses = bootstrap(api, config)
        verify(config, zone, ns, addresses)
    print("Panel URL: https://" + config["panelHostname"])
    print("Administrator: admin (initial bootstrap account)")
    print("Initial password: sudo k3s kubectl -n hosting get secret hostingpanel-bootstrap -o jsonpath='{.data.password}' | base64 -d; echo")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, subprocess.SubprocessError, KeyError) as error:
        # Never print subprocess exception objects that might include request material.
        print("Panel DNS bootstrap failed: " + (str(error) if isinstance(error, (ValueError, RuntimeError)) else type(error).__name__), file=sys.stderr)
        sys.exit(1)
