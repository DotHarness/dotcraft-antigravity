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
    public AntigravityBackend Backend { get; }
    public IModelProvider Provider { get; }
    public IChatClient Client { get; }
    public EffectiveModelRuntime Runtime { get; } = new("ag", "gemini-4-neon", "antigravity", "Antigravity", "", "", 30, null, new());

    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _supervisor;

    public static string Executable => Path.Combine(
        AppContext.BaseDirectory,
        "fake-cli",
        "DotCraft.Antigravity.FakeCli" + (OperatingSystem.IsWindows() ? ".exe" : ""));

    public BackendFixture(bool allowAiCredits = false, int idleTimeoutSeconds = 10, int requestTimeoutSeconds = 30)
    {
        Directory.CreateDirectory(Root);
        Backend = new AntigravityBackend(
            new AntigravitySettings(Executable, idleTimeoutSeconds, allowAiCredits, requestTimeoutSeconds),
            Root,
            _stopping.Token,
            configDirectory: Root,
            toolBatchWindow: TimeSpan.FromMilliseconds(40));
        Provider = Backend.CreateProvider(new ModelBackendContext(Runtime, new UnusedCredentials(), _stopping.Token));
        Client = Provider.CreateChatClient(Runtime);
        _supervisor = Backend.SuperviseAsync(_stopping.Token);
    }

    public static IDisposable Conversation(
        string thread = "thread",
        string window = "window",
        ProviderRequestKind kind = ProviderRequestKind.Turn) =>
        ProviderRequestContextScope.Push(new(new(thread, thread, null, null, "turn", window, kind, 0, "ag", null)));

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

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _stopping.CancelAsync();
        await _supervisor.WaitAsync(TimeSpan.FromSeconds(10));
        await Backend.DisposeAsync();
        _stopping.Dispose();
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
