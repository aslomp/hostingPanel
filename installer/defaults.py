#!/usr/bin/env python3
"""Resolve default server configuration without guessing disks or public DNS."""
import ipaddress
import json
import pathlib
import re
import subprocess
import sys
from generate import write, validate, hostname_valid


def network_defaults(routes, interfaces):
    route = routes[0]
    address = ipaddress.IPv4Address(route["prefsrc"])
    for interface in interfaces:
        if interface["ifname"] != route["dev"]:
            continue
        for info in interface["addr_info"]:
            if info.get("local") == str(address):
                # Single-node default: only this node is authorized for storage.
                return str(address), str(ipaddress.ip_network(f"{address}/32"))
    raise ValueError("No matching default-route IPv4 address")


def email_valid(value):
    return bool(re.fullmatch(r"[^\s@]+@[^\s@]+\.[^\s@]+", value)) and value.split("@")[-1].lower() not in {
        "example.com", "example.net", "example.org", "example.invalid"}


def defaults(template, address, network, email, mode, primary_dns="", panel_hostname=""):
    c = dict(template)
    c.update(clusterHost=address, storageHost=address, storageNetwork=network,
             webIp=address, acmeEmail=email, localStorage=True,
             panelHostname=panel_hostname, ns1=primary_dns, ns2="", poolDevices=[],
             confirmPoolCreation=False, buildPanelImage=True,
             acmeStaging=False)
    validate(c)
    return c


def ask_email():
    if not sys.stdin.isatty():
        raise SystemExit("ACME email requires an interactive terminal or explicit --config")
    email = input("ACME email address: ").strip()
    if not email_valid(email):
        raise SystemExit("A valid ACME email address is required")
    return email


def ask_hostname(prompt):
    if not sys.stdin.isatty():
        raise SystemExit("Missing hostname; provide an interactive terminal or explicit --config")
    value = input(prompt).strip().lower().rstrip(".")
    if not hostname_valid(value):
        raise SystemExit("A valid fully qualified DNS hostname is required")
    return value


def complete(c):
    if not c.get("acmeEmail"):
        c["acmeEmail"] = ask_email()
    if not c.get("ns1"):
        c["ns1"] = ask_hostname("Primary DNS nameserver (FQDN): ")
    if not c.get("panelHostname"):
        c["panelHostname"] = ask_hostname("HostingPanel hostname (FQDN): ")
    if c["ns1"] == c["panelHostname"]:
        raise ValueError("Primary DNS and panel hostnames must be distinct")
    validate(c)
    return c


def main():
    destination = pathlib.Path(sys.argv[1])
    mode = sys.argv[2]
    if mode == "--complete":
        c = json.loads(destination.read_text())
        write(destination, complete(c))
        return
    template = json.loads(pathlib.Path(__file__).with_name("cluster.example.json").read_text())
    routes = json.loads(subprocess.check_output(["ip", "-j", "-4", "route", "get", "1.1.1.1"], text=True))
    interfaces = json.loads(subprocess.check_output(["ip", "-j", "-4", "address", "show"], text=True))
    address, network = network_defaults(routes, interfaces)
    c = defaults(template, address, network, "", mode)
    if mode == "--full-server":
        c = complete(c)
    write(destination, c)
    print(f"Configuration saved to {destination}")


if __name__ == "__main__":
    main()
