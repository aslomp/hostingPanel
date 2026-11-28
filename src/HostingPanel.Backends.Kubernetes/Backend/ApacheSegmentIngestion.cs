using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HostingPanel.Api;

// A segment is immutable, bounded, and acknowledged only after the receipt,
// customer log rows and monthly aggregates commit together.
internal sealed class ApacheSegmentIngestion(ApacheLogCollector collector) : IApacheSegmentIngestion
{
    public async Task AcceptAsync(Guid segmentId,ReadOnlyMemory<byte> content,CancellationToken ct)
    {
        if(segmentId==Guid.Empty || content.Length is < 1 or > 1048576)
            throw new InvalidDataException("Invalid segment.");
        var digest=Convert.ToHexString(SHA256.HashData(content.Span));
        string text;
        try { text=new UTF8Encoding(false,true).GetString(content.Span); }
        catch(DecoderFallbackException) { throw new InvalidDataException("Invalid segment encoding."); }
        if(!text.EndsWith('\n')) throw new InvalidDataException("Incomplete segment.");
        var lines=text.Split('\n');
        if(lines.Length is < 2 or > 257) throw new InvalidDataException("Invalid segment size.");
        var batch=new List<ApacheLogCollector.Pending>(lines.Length-1);
        for(var i=0;i<lines.Length-1;i++)
        {
            var line=lines[i];
            var split=line.IndexOf(' ');
            if(split<1 || line.Length>16384 ||
               !DateTimeOffset.TryParseExact(line[..split],"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var timestamp) ||
               !ApacheLogCollector.TryDecode(line[(split+1)..],timestamp,out var row,out var access))
                throw new InvalidDataException("Invalid accounting record.");
            // Segment UUID + ordinal is stable across retries; the segment hash
            // also detects conflicting content under a reused UUID.
            batch.Add(new("segment/"+segmentId.ToString("N"),line[..split],digest,i+1,row!,access));
        }
        await collector.PersistAsync(batch,ct,segmentId,digest);
    }
}
