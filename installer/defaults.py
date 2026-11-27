#!/usr/bin/env python3
"""Resolve default server configuration without guessing disks or public DNS."""
import ipaddress
import json
import pathlib
import re
import socket
import subprocess
import sys
from generate import write, validate


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


def defaults(template, address, network, email, mode):
    c = dict(template)
    c.update(clusterHost=address, storageHost=address, storageNetwork=network,
             webIp=address, acmeEmail=email, localStorage=True,
             panelHostname="", ns1="", ns2="", poolDevices=[],
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


def main():
    destination = pathlib.Path(sys.argv[1])
    mode = sys.argv[2]
    if mode == "--email-only":
        c = json.loads(destination.read_text())
        c["acmeEmail"] = ask_email()
        validate(c)
        write(destination, c)
        return
    template = json.loads(pathlib.Path(__file__).with_name("cluster.example.json").read_text())
    routes = json.loads(subprocess.check_output(["ip", "-j", "-4", "route", "get", "1.1.1.1"], text=True))
    interfaces = json.loads(subprocess.check_output(["ip", "-j", "-4", "address", "show"], text=True))
    address, network = network_defaults(routes, interfaces)
    if mode == "--storage-server":
        # No ACME registration occurs on a dedicated storage host.
        email = ""
    else:
        email = ask_email()
    c = defaults(template, address, network, email, mode)
    write(destination, c)
    print(f"Configuration saved to {destination}")


if __name__ == "__main__":
    main()
