#!/usr/bin/env python3
"""Generate private installer inputs; JSON is valid YAML for Helm/kubectl."""
import ipaddress
import json
import pathlib
import re
import sys
import os
import tempfile

def write(path, value):
    path = pathlib.Path(path)
    fd, tmp = tempfile.mkstemp(dir=path.parent)
    try:
        with os.fdopen(fd, "w") as stream:
            json.dump(value, stream, indent=2)
            stream.write("\n")
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)

def validate(c):
    for k in ("clusterHost", "storageHost", "webIp"):
        if ipaddress.ip_address(c[k]).version != 4:
            raise ValueError(f"{k}: IPv4 required")
    net = ipaddress.ip_network(c["storageNetwork"])
    if net.version != 4 or net.prefixlen == 0 or ipaddress.ip_address(c["storageHost"]) not in net:
        raise ValueError("storageNetwork must be a restricted IPv4 network containing storageHost")
    for k in ("panelHostname", "ns1", "ns2"):
        if not re.fullmatch(r"[a-z0-9][a-z0-9.-]*\.[a-z0-9-]+", c[k]):
            raise ValueError(f"Invalid {k}")
    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]*", c["pool"]):
        raise ValueError("Invalid pool")
    if c["acmeEmail"] and (not re.fullmatch(r"[^\\s@]+@[^\\s@]+\\.[^\\s@]+", c["acmeEmail"]) or c["acmeEmail"].split("@")[-1].lower() in {"example.com","example.net","example.org","example.invalid"}):
        raise ValueError("Set real ACME email")
    for k in ("localStorage", "acmeStaging", "confirmPoolCreation", "buildPanelImage"):
        if type(c[k]) is not bool:
            raise ValueError(f"{k} must be a boolean")
    for k in ("k3sVersion", "helmVersion", "envoyVersion", "certManagerVersion", "csiVersion"):
        if not re.fullmatch(r"v?\d+\.\d+\.\d+(?:\+k3s\d+)?", c[k]):
            raise ValueError(f"{k} must be pinned")
    if c["agentTokenFile"] != "agent-token":
        raise ValueError("agentTokenFile must be agent-token")
    if not isinstance(c["poolDevices"], list) or len(c["poolDevices"]) > 2:
        raise ValueError("poolDevices must contain at most two disks")
    if len(set(c["poolDevices"])) != len(c["poolDevices"]):
        raise ValueError("Duplicate pool devices")
    if not c["acmeStaging"] and c["panelTag"] == "latest":
        raise ValueError("Production requires a versioned panel image")
    if c["localStorage"] and c["clusterHost"] != c["storageHost"]:
        raise ValueError("Local storage must use the cluster host address")

def generate(c, base):
    host, pool = c["storageHost"], c["pool"] + "/hosting"
    key = (base / "csi-key").read_text()
    chap = (base / "iscsi-chap").read_text().strip()
    if not re.fullmatch(r"[a-f0-9]{16}", chap):
        raise ValueError("Invalid iSCSI CHAP secret")
    for protocol in ("nfs", "iscsi"):
        zfs = dict(datasetParentName=pool + ("/nfs-volumes" if protocol == "nfs" else "/volumes"),
                   detachedSnapshotsDatasetParentName=pool + ("/nfs-snapshots" if protocol == "nfs" else "/snapshots"),
                   cli=dict(sudoEnabled=True))
        driver = dict(driver="zfs-generic-" + protocol,
                      sshConnection=dict(host=host, port=22, username="hosting-csi", privateKey=key),
                      zfs=zfs)
        if protocol == "nfs":
            zfs.update(datasetEnableQuotas=True, datasetEnableReservation=False,
                       datasetPermissionsMode="0755", datasetPermissionsUser=1654, datasetPermissionsGroup=1654)
            driver["nfs"] = dict(shareStrategy="setDatasetProperties", shareHost=host,
                shareStrategySetDatasetProperties=dict(properties=dict(sharenfs="rw=" + c["storageNetwork"] + ",root_squash,no_subtree_check")))
        else:
            zfs["zvolEnableReservation"] = True
            driver["iscsi"] = dict(shareStrategy="targetCli", targetPortal=host+":3260",
                shareStrategyTargetCli=dict(sudoEnabled=True, basename="iqn.2026-11.local.hosting",
                    tpg=dict(attributes=dict(authentication=1, generate_node_acls=1, cache_dynamic_acls=1, demo_mode_write_protect=0), auth=dict(userid="hosting-csi", password=chap))))
        values = dict(csiDriver=dict(name="org.democratic-csi."+protocol),
            driver=dict(config=driver),
            storageClasses=[dict(name="zfs-"+protocol, reclaimPolicy="Retain", volumeBindingMode="Immediate",
                allowVolumeExpansion=True, parameters=dict(fsType="nfs" if protocol=="nfs" else "ext4"),
                mountOptions=["nfsvers=4.1"] if protocol=="nfs" else [])])
        if protocol == "iscsi":
            values["storageClasses"][0]["secrets"] = {"node-stage-secret": {
                "node-db.node.session.auth.authmethod": "CHAP",
                "node-db.node.session.auth.username": "hosting-csi",
                "node-db.node.session.auth.password": chap}}
        write(base / ("csi-"+protocol+".json"), values)
    namespace = "envoy-gateway-system"
    def gateway(name, listeners):
        return dict(apiVersion="gateway.networking.k8s.io/v1", kind="Gateway",
            metadata=dict(name=name, namespace=namespace),
            spec=dict(gatewayClassName="eg", listeners=listeners))
    http = dict(name="http", protocol="HTTP", port=80, allowedRoutes={"namespaces":{"from":"All"}})
    https = dict(name="https", protocol="HTTPS", port=443, hostname=c["panelHostname"],
        tls=dict(mode="Terminate", certificateRefs=[dict(name="hosting-panel-tls")]),
        allowedRoutes=http["allowedRoutes"])
    external = gateway("external", [http, https] if c["panelHostname"] else [http])
    external["spec"]["allowedListeners"] = {"namespaces":{"from":"All"}}
    if c["panelHostname"]:
        external["metadata"]["annotations"] = {"cert-manager.io/cluster-issuer":"lets-encrypt"}
    # Internal gateway uses a ClusterIP proxy, selected via its own GatewayClass.
    internal = gateway("internal", [http])
    internal["spec"]["gatewayClassName"] = "eg-internal"
    resources = [
        dict(apiVersion="gateway.networking.k8s.io/v1", kind="GatewayClass", metadata=dict(name="eg"),
             spec=dict(controllerName="gateway.envoyproxy.io/gatewayclass-controller")),
        dict(apiVersion="gateway.envoyproxy.io/v1alpha1", kind="EnvoyProxy", metadata=dict(name="internal",namespace=namespace),
             spec=dict(provider=dict(type="Kubernetes",kubernetes=dict(envoyService=dict(type="ClusterIP"))))),
        dict(apiVersion="gateway.networking.k8s.io/v1", kind="GatewayClass", metadata=dict(name="eg-internal"),
             spec=dict(controllerName="gateway.envoyproxy.io/gatewayclass-controller",parametersRef=dict(group="gateway.envoyproxy.io",kind="EnvoyProxy",name="internal",namespace=namespace))),
        external, internal]
    write(base/"gateways.json", dict(apiVersion="v1",kind="List",items=resources))
    write(base/"issuer.json", dict(apiVersion="cert-manager.io/v1",kind="ClusterIssuer",metadata=dict(name="lets-encrypt"),
        spec=dict(acme=dict(email=c["acmeEmail"],server="https://acme-"+("staging-v02" if c["acmeStaging"] else "v02")+".api.letsencrypt.org/directory",
        privateKeySecretRef=dict(name="lets-encrypt-staging-account" if c["acmeStaging"] else "lets-encrypt-production-account"),solvers=[dict(http01=dict(gatewayHTTPRoute=dict(parentRefs=[dict(name="external",namespace=namespace,kind="Gateway",sectionName="http")])))]))))
    values = dict(backend="Kubernetes",web=dict(image=dict(repository=c["panelImage"],tag=c["panelTag"],pullPolicy="IfNotPresent")),
        bootstrap=dict(existingSecret="hostingpanel-bootstrap"),sqlite=dict(storageClassName="zfs-iscsi"),
        kubernetesHosting=dict(enabled=True,nfsServer=host,nfsPath="/srv/hosting/home",apacheConfigPath="/srv/hosting/apache",
            gatewayName="external",gatewayNamespace=namespace,panelRoute=dict(enabled=bool(c["panelHostname"]),hostname=c["panelHostname"],listener="https")),
        powerDns=dict(enabled=True,storageClassName="zfs-iscsi",webIp=c["webIp"],ns1=c["ns1"],ns2=c["ns2"],autoCreateZones=bool(c["ns1"] and c["ns2"])),
        certManager=dict(enabled=True),mariadb=dict(enabled=True,storageClassName="zfs-iscsi",existingSecret="hostingpanel-mariadb",provisioningSecret="hostingpanel-mariadb-provisioning"))
    write(base/"hosting-values.json",values)

if __name__ == "__main__":
    path = pathlib.Path(sys.argv[1])
    c = json.loads(path.read_text())
    validate(c)
    if len(sys.argv)>2 and sys.argv[2]=="generate":
        generate(c,path.parent)
