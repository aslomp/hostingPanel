using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

internal sealed class ApacheLogRow
{
    public long Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid DomainId { get; set; }
    public string Kind { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string Message { get; set; } = "";
}
internal sealed class ApacheLogReceipt
{
    public string Source { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string Digest { get; set; } = "";
    public int Occurrence { get; set; }
}
internal sealed class ApacheMonthlyUsage
{
    public Guid OwnerId { get; set; }
    public Guid DomainId { get; set; }
    public string Month { get; set; } = "";
    public long ReceivedBytes { get; set; }
    public long SentBytes { get; set; }
    public long Requests { get; set; }
}
internal sealed class ApacheSegmentReceipt
{
    public Guid Id { get; set; }
    public string Digest { get; set; } = "";
}
internal sealed class ApacheLogModel : IHostingPanelModelExtension, IHostingPanelSchemaExtension
{
    public string Provider => "Kubernetes.ApacheLogs";
    public IReadOnlyList<HostingPanelSchemaMigration> Migrations => [
        new(1, """
        CREATE TABLE IF NOT EXISTS "ApacheLogRows" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "OwnerId" TEXT NOT NULL, "DomainId" TEXT NOT NULL, "Kind" TEXT NOT NULL, "Timestamp" TEXT NOT NULL, "Message" TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS "IX_ApacheLogRows_DomainId_Kind_Id" ON "ApacheLogRows" ("DomainId","Kind","Id");
        CREATE TABLE IF NOT EXISTS "ApacheLogReceipts" ("Source" TEXT NOT NULL, "Timestamp" TEXT NOT NULL, "Digest" TEXT NOT NULL, "Occurrence" INTEGER NOT NULL, PRIMARY KEY ("Source","Timestamp","Digest","Occurrence"));
        CREATE TABLE IF NOT EXISTS "ApacheMonthlyUsage" ("OwnerId" TEXT NOT NULL, "DomainId" TEXT NOT NULL, "Month" TEXT NOT NULL, "ReceivedBytes" INTEGER NOT NULL, "SentBytes" INTEGER NOT NULL, "Requests" INTEGER NOT NULL, PRIMARY KEY ("OwnerId","DomainId","Month"));
        """),
        new(2, """CREATE TABLE IF NOT EXISTS "ApacheSegmentReceipts" ("Id" TEXT NOT NULL PRIMARY KEY, "Digest" TEXT NOT NULL);""")
    ];
    public void Configure(ModelBuilder model)
    {
        model.Entity<ApacheSegmentReceipt>(e => { e.ToTable("ApacheSegmentReceipts"); e.HasKey(x => x.Id); });
        model.Entity<ApacheLogRow>(e => { e.ToTable("ApacheLogRows"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.DomainId,x.Kind,x.Id }); });
        model.Entity<ApacheLogReceipt>(e => { e.ToTable("ApacheLogReceipts"); e.HasKey(x => new { x.Source,x.Timestamp,x.Digest,x.Occurrence }); });
        model.Entity<ApacheMonthlyUsage>(e => { e.ToTable("ApacheMonthlyUsage"); e.HasKey(x => new { x.OwnerId,x.DomainId,x.Month }); });
    }
}
