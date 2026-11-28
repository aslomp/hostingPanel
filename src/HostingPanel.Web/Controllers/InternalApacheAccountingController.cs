using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class InternalApacheAccountingController(IEnumerable<IApacheSegmentIngestion> providers,IConfiguration configuration) : ControllerBase
{
    [HttpPost("/hosting/internal/apache-accounting/{segmentId:guid}")]
    [RequestSizeLimit(1048576)]
    public async Task<IResult> Receive(Guid segmentId,CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("KubernetesHosting:DurableLogIngestion")) return Results.NotFound();
        var expected=configuration["KubernetesHosting:AccountingIngestionToken"];
        if(string.IsNullOrEmpty(expected) || expected.Length<32) return Results.StatusCode(503);
        var supplied=Request.Headers["X-HostingPanel-Accounting-Token"].ToString();
        if(supplied.Length!=expected.Length || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied),Encoding.UTF8.GetBytes(expected)))
            return Results.Unauthorized();
        var provider=providers.SingleOrDefault();
        if(provider is null) return Results.NotFound();
        using var buffer=new MemoryStream();
        var chunk=new byte[8192];
        while(true)
        {
            var read=await Request.Body.ReadAsync(chunk,ct);
            if(read==0) break;
            if(buffer.Length+read>1048576) return Results.StatusCode(413);
            buffer.Write(chunk,0,read);
        }
        try { await provider.AcceptAsync(segmentId,buffer.ToArray(),ct); }
        catch(InvalidDataException) { return Results.BadRequest(new { type="INVALID_ACCOUNTING_SEGMENT" }); }
        return Results.NoContent();
    }
}
