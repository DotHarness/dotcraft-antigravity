using System.Diagnostics;
using System.Text.Json;
using DotCraft.Agents;
using DotCraft.Configuration;
using Microsoft.Extensions.AI;
using Xunit;

namespace DotCraft.Antigravity.Tests;

internal sealed class BackendFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "dotcraft-agy-test-" + Guid.NewGuid().ToString("N"));
    public AntigravityBackend Backend { get; private set; } = null!;
    public IModelProvider Provider { get; private set; } = null!;
    public IChatClient Client { get; private set; } = null!;
    public EffectiveModelRuntime Runtime { get; private set; } = new("ag", "gemini-4-neon", "antigravity", "Antigravity", "", "", 30, null, new());

    private readonly AntigravitySettings _settings;
    private readonly TimeProvider? _clock;
    private CancellationTokenSource _stopping = null!;
    private Task _supervisor = null!;

    public static string Executable => Path.Combine(
        AppContext.BaseDirectory,
        "fake-cli",
        "DotCraft.Antigravity.FakeCli" + (OperatingSystem.IsWindows() ? ".exe" : ""));

    public BackendFixture(bool allowAiCredits = false, int idleTimeoutSeconds = 10, int requestTimeoutSeconds = 30, TimeProvider? clock = null)
    {
        _settings = new AntigravitySettings(Executable, idleTimeoutSeconds, allowAiCredits, requestTimeoutSeconds);
        _clock = clock;
        Start();
    }

    private void Start()
    {
        _stopping = new();
        Directory.CreateDirectory(Root);
        Backend = new AntigravityBackend(
            _settings,
            Root,
            _stopping.Token,
            clock: _clock,
            configDirectory: Root,
            toolBatchWindow: TimeSpan.FromMilliseconds(40));
        Provider = Backend.CreateProvider(new ModelBackendContext(Runtime, new UnusedCredentials(), _stopping.Token));
        Client = Provider.CreateChatClient(Runtime);
        _supervisor = Backend.SuperviseAsync(_stopping.Token);
    }

    public static IDisposable Conversation(
        string thread = "thread",
        string window = "window",
        ProviderRequestKind kind = ProviderRequestKind.Turn,
        string turn = "turn") =>
        ProviderRequestContextScope.Push(new(new(thread, thread, null, null, turn, window, kind, 0, "ag", null)));

    public JsonElement[] Files(string pattern) =>
        Directory.GetFiles(Root, pattern)
            .Select(p => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(p)))
            .ToArray();

    public JsonElement Capture => Assert.Single(Files("capture-*.json"));
    public int[] ChatPids => Files("capture-*.json").Select(c => c.GetProperty("pid").GetInt32()).ToArray();

    public static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        Runtime = Runtime with { BackendGeneration = Guid.NewGuid().ToString("N") };
        Start();
    }

    private async Task StopAsync()
    {
        Client.Dispose();
        await _stopping.CancelAsync();
        await _supervisor.WaitAsync(TimeSpan.FromSeconds(10));
        await Backend.DisposeAsync();
        _stopping.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (Directory.Exists(Root))
            Directory.Delete(Root, true);
    }

    private sealed class UnusedCredentials : IProviderCredentialStore
    {
        public Task<JsonElement?> ReadAsync(CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("CLI credentials must remain external.");
        public Task<JsonElement?> ModifyAsync(Func<JsonElement?, CancellationToken, Task<JsonElement?>> update, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("CLI credentials must remain external.");
        public Task DeleteAsync(CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("CLI credentials must remain external.");
    }
}
