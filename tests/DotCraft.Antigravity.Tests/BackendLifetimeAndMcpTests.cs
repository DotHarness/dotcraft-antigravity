using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DotCraft.Antigravity.Tests;

public sealed class BackendLifetimeAndMcpTests
{
    [Fact]
    public async Task CancellationKillsHangingCliProcessAndRemovesRunDirectory()
    {
        await using var fixture = new BackendFixture();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in fixture.Client.GetStreamingResponseAsync([new(ChatRole.User, "hang")], cancellationToken: cts.Token))
            {
                if (update.Text == "ready")
                    await cts.CancelAsync();
            }
        });

        Assert.False(BackendFixture.IsAlive(Assert.Single(fixture.ChatPids)));
        Assert.Empty(Directory.GetDirectories(fixture.Root, "run-*"));
    }

    [Fact]
    public async Task McpEndpointRequiresBearerTokenRejectsUnknownToolsAndReturnsRichResults()
    {
        using var stopping = new CancellationTokenSource();
        var received = new TaskCompletionSource<FunctionCallContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new AntigravityMcpServer(
            new AntigravityTools([AIFunctionFactory.Create((string value) => value, "ReadImage")]),
            call => received.TrySetResult(call),
            stopping.Token);
        await server.StartAsync(default);

        using var http = new HttpClient();
        using var unauthorized = await http.PostAsJsonAsync(server.Address, new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var authHeader = server.Configuration["mcpServers"]!["dotcraft"]!["headers"]!["Authorization"]!.GetValue<string>();
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(server.Address),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = authHeader }
        }));

        Assert.Equal("ReadImage", Assert.Single(await client.ListToolsAsync()).Name);
        var unknown = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("NotDeclared").AsTask());
        Assert.Equal(McpErrorCode.InvalidParams, unknown.ErrorCode);

        var pending = client.CallToolAsync("ReadImage", new Dictionary<string, object?> { ["value"] = "pic" }).AsTask();
        var call = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ReadImage", call.Name);
        Assert.False(pending.IsCompleted);

        server.Complete(
            call.CallId,
            new AIContent[] { new TextContent("image detail"), new DataContent(new byte[] { 1, 2 }, "image/png") },
            isError: true);

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsError);
        Assert.Equal("image detail", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(new byte[] { 1, 2 }, image.DecodedData.ToArray());

        Assert.Throws<InvalidDataException>(() => server.Complete("unknown", "nope"));
        await stopping.CancelAsync();
    }
}
