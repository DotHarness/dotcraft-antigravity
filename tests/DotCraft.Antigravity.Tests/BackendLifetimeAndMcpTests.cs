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

        var staleRoot = Path.Combine(fixture.Root, "stale-sweep");
        var staleRun = PrivateDirectory.Create(Path.Combine(staleRoot, "run-orphan"));
        using var sleeper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BackendFixture.Executable)
        {
            Arguments = "acp --process-per-session",
            WorkingDirectory = staleRun,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        await File.WriteAllTextAsync(
            Path.Combine(staleRun, AntigravityProcess.PidFileName),
            $"{sleeper.Id}:{sleeper.StartTime.ToUniversalTime().Ticks}");

        PrivateDirectory.SweepStale(staleRoot, "run-");

        Assert.True(sleeper.HasExited);
        Assert.False(Directory.Exists(staleRun));
    }

    [Fact]
    public async Task FirstOutputDisarmsTheStartupWatchdog()
    {
        await using var fixture = new BackendFixture(requestTimeoutSeconds: 2);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await using var updates = fixture.Client.GetStreamingResponseAsync([new(ChatRole.User, "hang")], cancellationToken: stop.Token)
            .GetAsyncEnumerator();

        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("ready", updates.Current.Text);

        stop.CancelAfter(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (await updates.MoveNextAsync()) { }
        });
    }

    [Fact]
    public async Task UserEchoAloneDoesNotDisarmTheStartupWatchdog()
    {
        await using var fixture = new BackendFixture(requestTimeoutSeconds: 2);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            fixture.Client.GetResponseAsync([new(ChatRole.User, "echo-only")], cancellationToken: deadline.Token));

        Assert.StartsWith("Antigravity.Timeout:", error.Message);
        Assert.Equal("echo-only", fixture.Capture.GetProperty("input").GetProperty("message").GetProperty("content").GetString());
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

        var authHeader = server.AuthorizationHeader;
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
