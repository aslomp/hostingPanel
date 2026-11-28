using HostingPanel.Api;

public static class SftpPolicyTests
{
    public static void Run()
    {
        var blob = new byte[51];
        new byte[] {0,0,0,11,115,115,104,45,101,100,50,53,53,49,57,0,0,0,32}.CopyTo(blob, 0);
        blob[50] = 1;
        var first = "ssh-ed25519 " + Convert.ToBase64String(blob);
        blob[50] = 2;
        var second = "ssh-ed25519 " + Convert.ToBase64String(blob);
        Require(KubernetesSftpAccounts.NormalizeKeys([first, second])?.Length == 2);
        Require(KubernetesSftpAccounts.NormalizeKeys([first, first + " comment"]) is null);
        Require(KubernetesSftpAccounts.NormalizeKeys([]) is null);
        var many = Enumerable.Range(1, 100).Select(i => { var b = (byte[])blob.Clone(); b[50] = (byte)i; return "ssh-ed25519 " + Convert.ToBase64String(b); }).ToArray();
        Require(KubernetesSftpAccounts.NormalizeKeys(many)?.Length == 100);
        Require(MailLimitPolicy.Resolve("{\"sftpKeys\":\"unlimited\"}", null, "sftpKeys") == long.MaxValue);
        Require(KubernetesSftpAccounts.NormalizeKeys([first + "\n" + second]) is null);
        Require(KubernetesSftpAccounts.DecodeKeys(first).SequenceEqual([first]));
        Require(KubernetesSftpAccounts.DecodeKeys(System.Text.Json.JsonSerializer.Serialize(new[] { first, second })).SequenceEqual([first, second]));
        Require(KubernetesSftpAccounts.ValidRelativePath("domains/example.nl/public_html"));
        Require(KubernetesSftpAccounts.ValidRelativePath(""));
        var id = Guid.NewGuid();
        var login = new OpenSshReconciler.Login("sftp-" + id.ToString("N"), 10042, 10042, [first], "customer/a", id, id);
        var snapshot = OpenSshReconciler.SnapshotWithPaths([login]);
        Require(OpenSshReconciler.Revision(snapshot).Length == 64);
        Require(OpenSshReconciler.Revision(snapshot) != OpenSshReconciler.Revision(OpenSshReconciler.SnapshotWithPaths([login with { SubPath = "customer/b" }])));
        foreach (var invalid in new[] { "../other", "/home/other", "a//b", "a/../b", "a\\b" })
            Require(!KubernetesSftpAccounts.ValidRelativePath(invalid));
    }
    private static void Require(bool value)
    {
        if (!value) throw new InvalidOperationException("SFTP policy assertion failed.");
    }
}
