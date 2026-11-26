using System.Text.Json;
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
        var args = resume.GetProperty("args").EnumerateArray().Select(arg => arg.GetString()).ToArray();
        Assert.Contains("--conversation", args);
        Assert.Equal(conversationId, args[Array.IndexOf(args, "--conversation") + 1]);
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
        var decoder = new AntigravityStreamDecoder(new AntigravityTools([]),
            new HashSet<string> { path }, new AntigravityUsageBaseline(), new AntigravityLimits(TimeProvider.System), "test-model");

        JsonElement ReadStep(string target) => JsonSerializer.SerializeToElement(new
        {
            @event = "step_update",
            step_update = new
            {
                step_type = "tool",
                tool_name = "read_file",
                tool_info = new { parameters = new { Path = target, Description = path } }
            }
        });

        Assert.Empty(decoder.Read(ReadStep(path), () => "").ToArray());
        Assert.Throws<NativeToolBlockedException>(() => decoder.Read(ReadStep(path + ".other"), () => "").ToArray());
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
