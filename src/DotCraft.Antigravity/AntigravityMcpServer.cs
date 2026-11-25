using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotCraft.Antigravity;

internal sealed class AntigravityMcpServer(
    AntigravityTools tools,
    Action<FunctionCallContent> onToolCall,
    CancellationToken stopping) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CallToolResult>> _pending = new(StringComparer.Ordinal);
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private WebApplication? _app;

    public string Address { get; private set; } = string.Empty;

    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(WebApplication).Assembly.FullName
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, 0);
            server.Limits.MaxRequestBodySize = 16 * 1024 * 1024;
        });
        builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = AntigravityTools.ServerName, Version = "0.1.5" })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools.ProtocolTools }))
            .WithCallToolHandler(CallAsync);

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            if (context.Request.Headers.Authorization != "Bearer " + _token)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
        _app.MapMcp("/mcp");
        await _app.StartAsync(ct);
        Address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single() + "/mcp";
    }

    public JsonObject Configuration => new()
    {
        ["mcpServers"] = new JsonObject
        {
            [AntigravityTools.ServerName] = new JsonObject
            {
                ["serverUrl"] = Address,
                ["url"] = Address,
                ["headers"] = new JsonObject
                {
                    ["Authorization"] = "Bearer " + _token
                },
                ["timeoutSeconds"] = 3600
            }
        }
    };

    public void Complete(string callId, object? result, bool isError = false)
    {
        if (!_pending.TryRemove(callId, out var pending)
            || !pending.TrySetResult(AntigravityContent.McpResult(result, isError)))
        {
            throw new InvalidDataException($"Antigravity.ToolResult: Unknown or duplicate tool result '{callId}'.");
        }
    }

    private async ValueTask<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        var request = context.Params;
        if (request is null || !tools.Contains(request.Name))
            throw new McpProtocolException("Declared DotCraft tool is required.", McpErrorCode.InvalidParams);

        var callId = $"agy_{Guid.NewGuid():N}";
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (request.Arguments is not null)
        {
            foreach (var (key, value) in request.Arguments)
                arguments[key] = value.Clone();
        }

        var tcs = new TaskCompletionSource<CallToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(callId, tcs))
            throw new McpProtocolException("Duplicate tool call ID.", McpErrorCode.InternalError);

        var call = new FunctionCallContent(callId, tools.HostName(request.Name), arguments);
        try
        {
            onToolCall(call);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping);
            return await tcs.Task.WaitAsync(linked.Token);
        }
        catch
        {
            _pending.TryRemove(callId, out _);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, pending) in _pending)
            pending.TrySetCanceled();
        _pending.Clear();

        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }
}
