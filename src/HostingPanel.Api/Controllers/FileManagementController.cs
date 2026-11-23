using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class FileManagementController(IHostingBackend backend) : BackendController
{
    [HttpPost("/api/filemanager-actions/chmod")]
    [HttpPost("/api/filemanager-actions/copy")]
    [HttpPost("/api/filemanager-actions/copy-to")]
    [HttpPost("/api/filemanager-actions/create-archive")]
    [HttpPost("/api/filemanager-actions/extract-archive")]
    [HttpPost("/api/filemanager-actions/mkdir")]
    [HttpPost("/api/filemanager-actions/move")]
    [HttpPost("/api/filemanager-actions/move-to")]
    [HttpPost("/api/filemanager-actions/remove")]
    [HttpDelete("/api/filemanager-actions/trash")]
    [HttpDelete("/api/filemanager-actions/trash/{id}")]
    [HttpPost("/api/filemanager-actions/trash/{id}/restore")]
    [HttpPost("/api/filemanager-actions/upload")]
    [HttpGet("/api/filemanager/disk-usage")]
    [HttpGet("/api/filemanager/download")]
    [HttpGet("/api/filemanager/download-archive")]
    [HttpGet("/api/filemanager/list")]
    [HttpGet("/api/filemanager/list-archive")]
    [HttpGet("/api/filemanager/search-files")]
    [HttpGet("/api/filemanager/search-text")]
    [HttpGet("/api/filemanager/stat")]
    [HttpGet("/api/filemanager/trash")]
    [HttpGet("/api/filemanager/tree")]
    public Task ForwardFileManagementAsync() => ForwardToBackendAsync(HttpContext, backend.Clients);


}
