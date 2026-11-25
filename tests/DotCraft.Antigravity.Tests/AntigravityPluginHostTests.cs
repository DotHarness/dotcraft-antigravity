using System.Text.Json;
using DotCraft.Agents;
using DotCraft.Configuration;
using DotCraft.Plugins;
using DotCraft.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DotCraft.Antigravity.Tests;

public sealed class AntigravityPluginHostTests
{
    [Fact]
    public async Task PackagedBackendActivatesThroughHostGatesBridgesToolsAndUnloadsCleanly()
    {
        var root = Path.Combine(Path.GetTempPath(), "antigravity-plugin-host-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var userData = Path.Combine(root, "user-data");
        const string id = "antigravity";
        var source = Path.Combine(AppContext.BaseDirectory, "antigravity-plugin");
        var destination = Path.Combine(workspace, ".craft", "plugins", id);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var path = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(file, path);
        }

        var parsed = PluginManifestParser.Load(destination);
        Assert.NotNull(parsed.Manifest?.Dotnet);
        Assert.DoesNotContain(parsed.Diagnostics, d => d.Severity == PluginDiagnosticSeverity.Error);

        Directory.CreateDirectory(userData);
        await File.WriteAllTextAsync(
            Path.Combine(userData, PluginConfigStore.FileName),
            JsonSerializer.Serialize(new Dictionary<string, object> { [id] = new { executablePath = BackendFixture.Executable } }));

        var config = new AppConfig
        {
            GlobalConfigPath = Path.Combine(userData, "config.json"),
            Providers = new() { ["ag"] = new() { BackendId = id } }
        };

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDotCraftRuntime(new() { Config = config, WorkspacePath = workspace, UserDataPath = userData });
        builder.Services.AddSingleton<IConfigSchemaProvider>(new EmptySchema());
        builder.Services.AddSingleton<IConfigDescriptorRegistry>(new EmptySchema());

        var host = builder.Build();
        try
        {
            await host.StartAsync();
            var runtime = host.Services.GetRequiredService<IDotNetExtensionRuntimeCoordinator>();
            await runtime.TrustAsync(id);
            await runtime.SetEnabledAsync(id, true);

            var active = Assert.Single(runtime.Snapshot.Plugins, p => p.PluginId == id);
            Assert.True(active.State == DotNetExtensionRuntimeState.Active, string.Join(" | ", active.Blockers.Select(b => b.Message)));

            var resolver = host.Services.GetRequiredService<ModelBackendResolver>();
            var descriptor = Assert.Single(resolver.GetBackends(), b => b.Id == id);
            Assert.Equal("Antigravity", descriptor.DisplayName);
            Assert.Single(descriptor.AuthenticationMethods, m => m.Id == "cli" && m.Kind == "cli");

            var clients = host.Services.GetRequiredService<ChatClientRegistry>();
            var client = clients.GetChatClient(config, "ag", "gemini-4-neon");
            Assert.Equal(ModelSteeringMode.AtNextBoundary, client.GetService<ModelSteeringPolicy>()?.Mode);
            using var scope = BackendFixture.Conversation();
            var result = await client.GetResponseAsync([new(ChatRole.User, "parallel")], BackendSamplingTests.Options())
                .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(ChatFinishReason.ToolCalls, result.FinishReason);
            Assert.Equal(2, result.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count());

            var catalog = await ModelProviderCatalog.FetchAsync(config, resolver, "ag");
            Assert.True(catalog.Success);
            Assert.Equal("gemini-4-neon", catalog.Models[0].Id);

            using (BackendFixture.Conversation(thread: "completed"))
            {
                var complete = await client.GetResponseAsync([new(ChatRole.User, "hello")])
                    .WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(ChatFinishReason.Stop, complete.FinishReason);
            }

            await runtime.SetEnabledAsync(id, false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (runtime.Snapshot.Plugins.Single(p => p.PluginId == id).State != DotNetExtensionRuntimeState.Stopped)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(100, timeout.Token);
            }
            Assert.Equal(0, runtime.Snapshot.Plugins.Single(p => p.PluginId == id).LeakedGenerations);

            var dataRoot = AntigravitySessions.WorkspaceRoot(Path.Combine(userData, "plugin-data", id), workspace);
            var saved = Assert.Single(Directory.GetDirectories(dataRoot, "run-*"));
            Assert.True(File.Exists(Path.Combine(saved, AntigravitySessions.CheckpointFile)));
            foreach (var file in Directory.GetFiles(dataRoot, "started-*.json"))
            {
                var pid = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(file)).GetProperty("pid").GetInt32();
                Assert.False(BackendFixture.IsAlive(pid));
            }
        }
        finally
        {
            await host.StopAsync();
            await ((IAsyncDisposable)host).DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    private sealed class EmptySchema : IConfigSchemaProvider, IConfigDescriptorRegistry
    {
        public IReadOnlyList<ConfigSchemaSection> GetConfigSchema() => [];
        public IReadOnlyList<ConfigFieldDescriptor> Fields => [];
    }
}
