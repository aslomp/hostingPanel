using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
namespace HostingPanel.Providers.CertManager;

// CertificateRequests contain public certificates, not private keys. No Secret access is needed.
public sealed class CertificateSnapshot(JsonElement[] certificates, JsonElement[] requests, JsonElement[] orders, JsonElement[] challenges)
{
    public static CertificateSnapshot Empty { get; } = new([], [], [], []);
    private static string Get(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static JsonElement Obj(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    private static JsonElement[] ArrayOf(JsonElement e, string key) => Obj(e,key) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToArray() : [];
    private static JsonElement Meta(JsonElement e) => Obj(e,"metadata");
    private static string Uid(JsonElement e) => Get(Meta(e),"uid");
    private static bool Owned(JsonElement e, JsonElement parent, string kind) => Uid(parent).Length > 0 && ArrayOf(Meta(e),"ownerReferences").Any(x => Get(x,"kind")==kind && Get(x,"uid")==Uid(parent));
    private static bool Condition(JsonElement e,string type) => ArrayOf(Obj(e,"status"),"conditions").Any(x => Get(x,"type")==type && Get(x,"status")=="True");
    private JsonElement Certificate(Guid id) => certificates.FirstOrDefault(x => Get(Meta(x),"name")==CertManagerClient.Name(id));
    private JsonElement[] Requests(JsonElement cert) => requests.Where(x => Owned(x,cert,"Certificate")).OrderByDescending(x => Get(Meta(x),"creationTimestamp"),StringComparer.Ordinal).ToArray();
    public static async Task<CertificateSnapshot> LoadAsync(CertManagerClient client,CancellationToken ct)
    {
        static JsonElement[] Parse(string json) { using var doc=JsonDocument.Parse(json); return ArrayOf(doc.RootElement,"items").Select(x=>x.Clone()).ToArray(); }
        return new(Parse(await client.ListAsync(ct)),
            Parse(await client.ListRequestsAsync(ct)),
            Parse(await client.ListOrdersAsync(ct)),
            Parse(await client.ListChallengesAsync(ct)));
    }
    public CertificateInfo? Find(Guid id)
    {
        var cert=Certificate(id);
        if (cert.ValueKind == JsonValueKind.Undefined) return null;
        foreach(var request in Requests(cert))
        {
            var encoded=Get(Obj(request,"status"),"certificate");
            if(encoded.Length==0) continue;
            try {
                using var x509=X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
                var san=x509.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
                var dns=san?.EnumerateDnsNames().ToArray() ?? [];
                var ips=san?.EnumerateIPAddresses().Select(x=>x.ToString()).ToArray() ?? [];
                var now=DateTime.UtcNow;
                var error=x509.NotAfter.ToUniversalTime()<=now ? "expired" : x509.NotBefore.ToUniversalTime()>now ? "other" : "";
                return new(CertManagerClient.Name(id),dns,error,Condition(cert,"Issuing"),new { dnsNames=dns,ipAddresses=ips,issuer=x509.Issuer,subject=x509.Subject,
                    notAfter=x509.NotAfter.ToUniversalTime(),notBefore=x509.NotBefore.ToUniversalTime(),prettyType=x509.PublicKey.Oid.FriendlyName ?? x509.PublicKey.Oid.Value ?? "",serialNumber=x509.SerialNumber });
            } catch (Exception ex) when(ex is CryptographicException or FormatException) { }
        }
        return new(CertManagerClient.Name(id),[],"other",Condition(cert,"Issuing"),null);
    }
    public sealed record CertificateInfo(string Id,string[] DnsNames,string Error,bool Issuing,object? PublicCertificate)
    {
        public bool Valid => Error.Length==0 && PublicCertificate is not null;
        public bool Covers(string name) => Valid && DnsNames.Any(d => d.Equals(name,StringComparison.OrdinalIgnoreCase) ||
            (!name.StartsWith("*.",StringComparison.Ordinal) && d.StartsWith("*.",StringComparison.Ordinal) && name.EndsWith(d[1..],StringComparison.OrdinalIgnoreCase) && name.Count(c=>c=='.')==d.Count(c=>c=='.')));
    }
    public sealed record Log(int Id,object Summary,object Detail);
    public Log[] Logs(Guid id)
    {
        var result=new List<Log>();
        foreach(var request in Requests(Certificate(id)))
        foreach(var order in orders.Where(o=>Owned(o,request,"CertificateRequest")))
        foreach(var challenge in challenges.Where(c=>Owned(c,order,"Order")))
        {
            var spec=Obj(challenge,"spec"); var status=Obj(challenge,"status");
            var type=Get(spec,"type"); if(type is not ("HTTP-01" or "DNS-01")) continue;
            var uid=Uid(challenge); if(uid.Length==0) continue;
            var logId=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(uid))) & int.MaxValue;
            var state=Get(status,"state");
            var outcome=state switch { "valid"=>"success","invalid" or "expired" or "errored"=>"failure","pending"=>"running",_=>"pending" };
            var name=Get(spec,"dnsName");
            var created=Get(Meta(challenge),"creationTimestamp");
            var certID=CertManagerClient.Name(id);
            var summary=new { id=logId,certID,challengeType=type,createTime=created,dnsNames=new[]{name},status=outcome };
            // Do not expose raw solver output, tokens, credentials or arbitrary Kubernetes messages.
            var detail=new { id=logId,certID,challengeType=type,createTime=created,dnsNames=new[]{name},status=outcome,output="cert-manager ACME challenge state: "+state };
            result.Add(new(logId,summary,detail));
        }
        return result.GroupBy(x=>x.Id).Where(g=>g.Count()==1).Select(g=>g.Single()).ToArray();
    }
}
