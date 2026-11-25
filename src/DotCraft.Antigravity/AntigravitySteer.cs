using System.Text.Json.Nodes;

namespace DotCraft.Antigravity;

internal sealed class AntigravitySteer(PrivateDirectory directory)
{
    private static readonly string Config = Path.Combine("home", ".gemini", "config");
    private static readonly string Pending = Path.Combine(Config, "dotcraft-steer.json");

    public Task InstallAsync(CancellationToken ct)
    {
        var command = OperatingSystem.IsWindows()
            ? """
                if exist "%USERPROFILE%\.gemini\config\dotcraft-steer.json" (move /Y "%USERPROFILE%\.gemini\config\dotcraft-steer.json" "%USERPROFILE%\.gemini\config\dotcraft-steer.reading" >nul && type "%USERPROFILE%\.gemini\config\dotcraft-steer.reading" && del "%USERPROFILE%\.gemini\config\dotcraft-steer.reading") else echo {}
                """
            : """
                pending="$HOME/.gemini/config/dotcraft-steer.json"; claimed="$HOME/.gemini/config/dotcraft-steer.reading"
                if [ -f "$pending" ]; then mv "$pending" "$claimed" && cat "$claimed" && rm "$claimed"; else printf '%s\n' '{}'; fi
                """;
        var hook = new JsonObject
        {
            ["dotcraft-steer"] = new JsonObject
            {
                ["PreInvocation"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command
                })
            }
        };
        return directory.WriteAsync(Path.Combine(Config, "hooks.json"), hook.ToJsonString(), ct);
    }

    public Task PublishAsync(JsonArray steps, CancellationToken ct) =>
        directory.WriteAtomicAsync(Pending, new JsonObject { ["injectSteps"] = steps }.ToJsonString(), ct);

    public void VerifyDelivered()
    {
        if (File.Exists(Path.Combine(directory.Path, Pending))
            || File.Exists(Path.Combine(directory.Path, Config, "dotcraft-steer.reading")))
        {
            throw new InvalidDataException("Antigravity.SteerNotDelivered: The native hook did not take the pending input.");
        }
    }
}
