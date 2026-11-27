using System.IO.Pipes;
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
        var clock = new WatchdogClock();
        await using var fixture = new BackendFixture(clock: clock);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var updates = fixture.Client.GetStreamingResponseAsync([new(ChatRole.User, "hang")], cancellationToken: stop.Token)
            .GetAsyncEnumerator();

        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("ready", updates.Current.Text);

        await clock.Stopped.WaitAsync(stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (await updates.MoveNextAsync()) { }
        });
    }

    [Fact]
    public async Task UserEchoAloneDoesNotDisarmTheStartupWatchdog()
    {
        var clock = new WatchdogClock();
        await using var fixture = new BackendFixture(clock: clock);
        var pipeName = $"agy-test-{Guid.NewGuid():N}";
        using var echoed = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = fixture.Client.GetResponseAsync([new(ChatRole.User, $"echo-only {pipeName}")], cancellationToken: deadline.Token);

        await echoed.WaitForConnectionAsync(deadline.Token);
        clock.Advance(TimeSpan.FromSeconds(31));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => response);
        Assert.StartsWith("Antigravity.Timeout:", error.Message);
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

    private sealed class WatchdogClock : TimeProvider
    {
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _tick;
        private long _timestamp;

        public Task Stopped => _stopped.Task;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref _timestamp, elapsed.Ticks);
            Volatile.Read(ref _tick)?.Invoke();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Volatile.Write(ref _tick, () => callback(state));
            return new WatchdogTimer(this);
        }

        private sealed class WatchdogTimer(WatchdogClock clock) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose()
            {
                Volatile.Write(ref clock._tick, null);
                clock._stopped.TrySetResult();
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
