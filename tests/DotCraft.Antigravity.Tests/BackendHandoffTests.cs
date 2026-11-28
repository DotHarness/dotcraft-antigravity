using System.Text.Json;
using DotHarness.Acp.V1;
using Microsoft.Extensions.AI;
using Xunit;

namespace DotCraft.Antigravity.Tests;

public sealed class BackendHandoffTests
{
    [Fact]
    public async Task ColdHandoffKeepsRecentToolRoundInlineAndResumesAfterRestart()
    {
        await using var fixture = new BackendFixture(idleTimeoutSeconds: 120);
        using var scope = BackendFixture.Conversation();
        const string archivedCall = "archive_probe";
        const string archivedResult = "archive-only-evidence";
        const string recentCall = "current_probe";
        const string recentResult = "current-check-result";
        const string recentProgress = "The checks are complete. Publishing is next.";
        const string currentInput = "Continue with the next step.";
        const string nextInput = "Confirm the final status.";
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Inspect the initial state."),
            new(ChatRole.Assistant, [new FunctionCallContent("call-archive", archivedCall)]),
            new(ChatRole.Tool, [new FunctionResultContent("call-archive", new string('x', 48 * 1024) + archivedResult)]),
            new(ChatRole.User, "Check the current state."),
            new(ChatRole.Assistant, [new FunctionCallContent("call-recent", recentCall)]),
            new(ChatRole.Tool, [new FunctionResultContent("call-recent", recentResult)]),
            new(ChatRole.Assistant, recentProgress),
            new(ChatRole.User, currentInput)
        };

        var first = await fixture.Client.GetResponseAsync(history).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(ChatFinishReason.Stop, first.FinishReason);
        var conversationId = Assert.IsType<string>(first.ResponseId);
        var cold = fixture.Capture;
        var work = cold.GetProperty("cwd").GetString()!;
        var handoff = Path.Combine(work, "conversation-handoff.txt");
        var prompt = Input(cold);
        Assert.Contains(Path.GetFileName(handoff), prompt);
        Assert.DoesNotContain(archivedCall, prompt);
        Assert.DoesNotContain(archivedResult, prompt);
        Assert.Contains(recentCall, prompt);
        Assert.Contains(recentResult, prompt);
        Assert.Contains(recentProgress, prompt);
        Assert.Contains(currentInput, prompt);

        var archive = await File.ReadAllTextAsync(handoff);
        Assert.Contains(archivedCall, archive);
        Assert.Contains(archivedResult, archive);
        Assert.DoesNotContain(recentCall, archive);
        Assert.DoesNotContain(recentResult, archive);
        Assert.DoesNotContain(recentProgress, archive);
        Assert.DoesNotContain(currentInput, archive);
        AssertReadAccess(cold, handoff);

        await fixture.RestartAsync();
        history.AddRange(first.Messages);
        history.Add(new(ChatRole.User, nextInput));

        var next = await fixture.Client.GetResponseAsync(history).WaitAsync(TimeSpan.FromSeconds(15));

        var coldPid = cold.GetProperty("pid").GetInt32();
        var resume = Assert.Single(fixture.Files("capture-*.json"),
            capture => capture.GetProperty("pid").GetInt32() != coldPid);
        Assert.Equal(conversationId, resume.GetProperty("resume").GetString());
        Assert.Equal(conversationId, next.ResponseId);
        Assert.Equal(work, resume.GetProperty("cwd").GetString());
        Assert.Equal(nextInput, Input(resume));
        AssertReadAccess(resume, handoff);
        Assert.Equal(archive, await File.ReadAllTextAsync(handoff));
    }

    [Fact]
    public void FileReadMustTargetExactlyTheAuthorizedHandoff()
    {
        var path = Path.Combine(Path.GetTempPath(), "handoff.txt");
        var paths = new HashSet<string> { path };
        var tools = new AntigravityTools([]);
        ToolCall ReadStep(string target) => new()
        {
            ToolCallId = "read-1",
            Name = "read_file",
            Title = "File " + Path.GetFileName(path),
            RawInput = JsonSerializer.SerializeToElement(new { Path = target })
        };

        Assert.True(AntigravityAcpToolPolicy.Allows(ReadStep(path), tools, paths));
        Assert.False(AntigravityAcpToolPolicy.Allows(ReadStep(path + ".other"), tools, paths));
    }

    private static string Input(JsonElement capture) =>
        capture.GetProperty("input").GetProperty("message").GetProperty("content").GetString()!;

    private static void AssertReadAccess(JsonElement capture, string path)
    {
        var readRules = capture.GetProperty("settings").GetProperty("permissions").GetProperty("allow")
            .EnumerateArray()
            .Select(rule => rule.GetString()!.Replace('\\', '/'))
            .Where(rule => rule.StartsWith("read_file(", StringComparison.Ordinal)
                || rule.StartsWith("view_file(", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(readRules);
        var exactPath = path.Replace('\\', '/');
        var expectedRules = new[] { $"read_file({exactPath})", $"view_file({exactPath})" };
        Assert.All(readRules, rule => Assert.Contains(rule, expectedRules));

        var agent = capture.GetProperty("agentMarkdown").GetString()!;
        Assert.Matches(@"(?m)^tools:[ \t]*(?:\[[ \t]*view_file[ \t]*\]|\r?\n[ \t]*-[ \t]+view_file)[ \t]*\r?$", agent);
    }
}
