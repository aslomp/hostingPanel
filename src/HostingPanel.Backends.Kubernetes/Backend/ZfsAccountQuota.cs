using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace HostingPanel.Api;

public sealed class ZfsAccountQuota(IConfiguration configuration)
{
    public static bool Valid(string value,string field) => value=="unlimited" ||
        (long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var n) && n>0 &&
         (field!="quota" || n<=long.MaxValue/1048576));

    public static string Resolve(string? package,string? account,string field)
    {
        static Dictionary<string,string> Read(string? json) => JsonSerializer.Deserialize<Dictionary<string,string>>(json??"{}")??new();
        var value=Read(account).GetValueOrDefault(field)??Read(package).GetValueOrDefault(field);
        if(value is null || !Valid(value,field)) throw new InvalidOperationException("Explicit positive or unlimited storage entitlement required.");
        return value=="unlimited" ? "none" : checked(long.Parse(value,CultureInfo.InvariantCulture)*(field=="quota"?1048576L:1L)).ToString(CultureInfo.InvariantCulture);
    }

    public async Task ApplyAsync(string username,int uid,string bytes,string objects,CancellationToken ct)
    {
        if(!KubernetesInputValidation.ValidUser(username) || uid<1000) throw new InvalidOperationException("Invalid quota identity.");
        var host=configuration["ZfsQuota:Host"];
        if(string.IsNullOrEmpty(host) || !host.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '-') || host.StartsWith('-'))
            throw new InvalidOperationException("Invalid quota host.");
        var temp=Path.Combine(Path.GetTempPath(),"hp-quota-"+Guid.NewGuid().ToString("N"));
        if(!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(temp,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        try
        {
            var key=Path.Combine(temp,"key");
            await File.WriteAllBytesAsync(key,await File.ReadAllBytesAsync("/run/zfs-quota/private-key",ct),ct);
            File.SetUnixFileMode(key,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            var start=new ProcessStartInfo("/usr/bin/ssh") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true };
            foreach(var arg in new[]{"-T","-F","/dev/null","-o","BatchMode=yes","-o","StrictHostKeyChecking=yes","-o","UserKnownHostsFile=/run/zfs-quota/known-hosts","-o","GlobalKnownHostsFile=/dev/null","-o","IdentitiesOnly=yes","-o","ConnectTimeout=10","-i",key,"hosting-quota@"+host,"apply",username,uid.ToString(CultureInfo.InvariantCulture),bytes,objects}) start.ArgumentList.Add(arg);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var process=Process.Start(start)??throw new InvalidOperationException("Cannot start quota transport.");
            try
            {
                var output=process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error=process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                if(process.ExitCode!=0 || (await output).Trim()!="OK") throw new InvalidOperationException("Storage quota not confirmed.");
                await error;
            }
            finally { if(!process.HasExited) process.Kill(true); }
        }
        finally { Directory.Delete(temp,true); }
    }
}
