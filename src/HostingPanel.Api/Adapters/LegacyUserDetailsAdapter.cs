using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HostingPanel.Api.Adapters;

public static class LegacyUserDetailsAdapter
{
    public static IReadOnlyDictionary<string, string> Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) throw new FormatException("Empty DirectAdmin response.");
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (response.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("Expected object.");
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
                    data[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
        }
        else
        {
            foreach (var pair in response.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = pair.IndexOf('=');
                if (i < 1) throw new FormatException("Invalid legacy form response.");
                data[System.Net.WebUtility.UrlDecode(pair[..i])] = System.Net.WebUtility.UrlDecode(pair[(i+1)..]);
            }
        }
        if (data.TryGetValue("error", out var error) && error is "1" or "true")
            throw new LegacyApiException("DirectAdmin rejected the user details request.");
        if (data.Count == 0) throw new FormatException("Empty legacy fields.");
        return data;
    }

    public static JsonObject ConvertConfig(IReadOnlyDictionary<string,string> fields)
    {
        var o = new JsonObject();
        foreach(var (target, source) in new (string, string)[] {
            ("username","username"), ("userType","usertype"), ("creator","creator"), ("email","email"),
            ("name","name"), ("domain","domain"), ("dateCreated","date_created"), ("ip","ip"),
            ("package","package"), ("language","language"), ("skin","skin"), ("ns1","ns1"),
            ("ns2","ns2"), ("mailPartition","mail_partition"), ("mySqlConf","mysql_conf"),
            ("suspendedReason","suspended_reason") })
            if(fields.TryGetValue(source,out var v)) o[target] = v;

        foreach(var (target,source) in new (string,string)[] {
            ("account","account"), ("aftp","aftp"), ("catchAll","catchall"), ("cgi","cgi"),
            ("clamav","clamav"), ("cron","cron"), ("dnsControl","dnscontrol"),
            ("git","git"), ("jail","jail"), ("loginKeys","login_keys"), ("nginxUnit","nginx_unit"),
            ("php","php"), ("redis","redis"), ("spam","spam"), ("ssh","ssh"), ("ssl","ssl"),
            ("sysInfo","sysinfo"), ("suspended","suspended"), ("twoStepAuth","twostep_auth"),
            ("twoStepAuthReportFailures","notify_on_all_twostep_auth_failures"),
            ("wordpress","wordpress") })
            if(fields.TryGetValue(source,out var v) && ParseBool(v) is bool b) o[target] = b;

        foreach(var (target,source) in new (string,string)[] {
            ("autorespondersLim","nemailr"), ("domainPointersLim","domainptr"),
            ("domainsLim","vdomains"), ("emailAccountsLim","nemails"),
            ("emailForwardersLim","nemailf"), ("ftpAccountsLim","ftp"),
            ("inodeLim","inode"), ("mailingListsLim","nemailml"),
            ("mySqlDatabasesLim","mysql"), ("subdomainsLim","nsubdomains"),
            ("usersLim","nusers"), ("letsEncrypt","letsencrypt"), ("zoom","zoom"),
            ("usersManageDomains","users_manage_domains") })
            if(fields.TryGetValue(source,out var v) && ParseQuantity(v,false) is long n
                && n >= int.MinValue && n <= int.MaxValue) o[target] = n;

        foreach(var (target,source) in new (string,string)[] {
            ("bandwidthLim","bandwidth"),("quotaLim","quota") })
            if(fields.TryGetValue(source,out var v))
            {
                if(v.Equals("unlimited",StringComparison.OrdinalIgnoreCase)) o[target] = -1;
                else if(double.TryParse(v,NumberStyles.Float,CultureInfo.InvariantCulture,out var d)
                    && double.IsFinite(d)) o[target] = d;
            }
        return o;
    }

    public static JsonObject ConvertUsage(IReadOnlyDictionary<string,string> used,
        IReadOnlyDictionary<string,string> config)
    {
        var o = new JsonObject();
        foreach(var (target,source,mib) in new (string,string,bool)[] {
            ("autoresponders","nemailr",false), ("bandwidthBytes","bandwidth",true),
            ("domainPointers","domainptr",false), ("domains","vdomains",false),
            ("emailAccounts","nemails",false), ("emailForwarders","nemailf",false),
            ("ftpAccounts","ftp",false), ("inode","inode",false),
            ("mailingLists","nemailml",false), ("mySqlDatabases","mysql",false),
            ("quotaBytes","quota",true), ("subdomains","nsubdomains",false) })
            if(used.TryGetValue(source,out var usage) && config.TryGetValue(source,out var maximum)
               && ParseQuantity(usage,mib) is long u && ParseQuantity(maximum,mib) is long limit)
                o[target] = new JsonObject { ["usage"] = u, ["limit"] = limit, ["unlimited"] = limit == -1 };

        foreach(var key in new [] {"dbQuotaBytes","emailDeliveries","emailDeliveriesIncoming",
            "emailDeliveriesOutgoing","emailQuotaBytes","otherQuotaBytes","quotaWithoutSystemBytes"})
            if(used.TryGetValue(key,out var v) && long.TryParse(v,NumberStyles.Integer,
                CultureInfo.InvariantCulture,out var number)) o[key] = number;
        return o;
    }

    public static string[] MissingRequired(JsonObject result, IReadOnlyList<string> required) =>
        required.Where(key => !result.ContainsKey(key)).ToArray();

    private static bool? ParseBool(string v) => v.ToUpperInvariant() switch {
        "ON" or "YES" or "TRUE" or "1" => true,
        "OFF" or "NO" or "FALSE" or "0" => false, _ => null
    };
    private static long? ParseQuantity(string v,bool mib)
    {
        if(v.Equals("unlimited",StringComparison.OrdinalIgnoreCase)) return -1;
        if(!decimal.TryParse(v,NumberStyles.Float,CultureInfo.InvariantCulture,out var d) || d < 0)
            return null;
        try { return checked((long)decimal.Truncate(d * (mib ? 1048576m : 1m))); }
        catch (OverflowException) { return null; }
    }
}
