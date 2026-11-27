#!/usr/bin/env python3
"""Generate private installer inputs; JSON is valid YAML for Helm/kubectl."""
import ipaddress
import json
import pathlib
import re
import sys

def write(path, value):
    path = pathlib.Path(path)
    with path.open("w") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")
    path.chmod(0o600)

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
    if "@" not in c["acmeEmail"] or c["acmeEmail"].endswith("example.com"):
        raise ValueError("Set real ACME email")
    if c["localStorage"] and c["clusterHost"] != c["storageHost"]:
        raise ValueError("Local storage must use the cluster host address")

def generate(c, base):
    host, pool = c["storageHost"], c["pool"] + "/hosting"
    key = (base / "csi-key").read_text()
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
                    tpg=dict(attributes=dict(authentication=0, generate_node_acls=1, cache_dynamic_acls=1, demo_mode_write_protect=0))))
        values = dict(csiDriver=dict(name="org.democratic-csi."+protocol),
            driver=dict(config=driver),
            storageClasses=[dict(name="zfs-"+protocol, reclaimPolicy="Retain", volumeBindingMode="Immediate",
                allowVolumeExpansion=True, parameters=dict(fsType="nfs" if protocol=="nfs" else "ext4"),
                mountOptions=["nfsvers=4.1"] if protocol=="nfs" else [])])
        write(base / ("csi-"+protocol+".json"), values)
    namespace = "envoy-gateway-system"
    def gateway(name, listeners):
        return dict(apiVersion="gateway.networking.k8s.io/v1", kind="Gateway",
            metadata=dict(name=name, namespace=namespace),
            spec=dict(gatewayClassName="eg", listeners=listeners))
    http = dict(name="http", protocol="HTTP", port=80, allowedRoutes=dict(namespaces=dict(from_="All")))
    http["allowedRoutes"]["namespaces"] = {"from":"All"}
    https = dict(name="https", protocol="HTTPS", port=443, hostname=c["panelHostname"],
        tls=dict(mode="Terminate", certificateRefs=[dict(name="hosting-panel-tls")]),
        allowedRoutes=http["allowedRoutes"])
    external = gateway("external", [http, https])
    external["spec"]["allowedListeners"] = {"namespaces":{"from":"All"}}
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
        privateKeySecretRef=dict(name="lets-encrypt-account"),solvers=[dict(http01=dict(gatewayHTTPRoute=dict(parentRefs=[dict(name="external",namespace=namespace,kind="Gateway",sectionName="http")])))]))))
    values = dict(backend="Kubernetes",web=dict(image=dict(repository=c["panelImage"],tag=c["panelTag"],pullPolicy="IfNotPresent")),
        bootstrap=dict(existingSecret="hostingpanel-bootstrap"),sqlite=dict(storageClassName="zfs-iscsi"),
        kubernetesHosting=dict(enabled=True,nfsServer=host,nfsPath="/srv/hosting/home",apacheConfigPath="/srv/hosting/apache",
            gatewayName="external",gatewayNamespace=namespace,panelRoute=dict(enabled=True,hostname=c["panelHostname"],listener="https")),
        powerDns=dict(enabled=True,storageClassName="zfs-iscsi",webIp=c["webIp"],ns1=c["ns1"],ns2=c["ns2"]),
        certManager=dict(enabled=True),mariadb=dict(enabled=True,storageClassName="zfs-iscsi",existingSecret="hostingpanel-mariadb",provisioningSecret="hostingpanel-mariadb-provisioning"))
    write(base/"hosting-values.json",values)

if __name__ == "__main__":
    path = pathlib.Path(sys.argv[1])
    c = json.loads(path.read_text())
    validate(c)
    if len(sys.argv)>2 and sys.argv[2]=="generate":
        generate(c,path.parent)
