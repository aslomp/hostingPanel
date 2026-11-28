namespace HostingPanel.Api;

public interface IApacheSegmentIngestion
{
    Task AcceptAsync(Guid segmentId, ReadOnlyMemory<byte> content, CancellationToken ct);
}
