using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = Encoding.UTF8;

var cwd = Environment.CurrentDirectory;
var runDir = Directory.GetParent(cwd)!.FullName;
var root = Environment.GetEnvironmentVariable("ANTIGRAVITY_CONFIG_DIR") is { Length: > 0 } cfg
    ? cfg
    : Directory.GetParent(runDir)!.FullName;
Directory.CreateDirectory(root);

var pid = Environment.ProcessId;
var startTicks = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
var accountPath = Path.Combine(root, "fake-account.json");
await File.WriteAllTextAsync(Path.Combine(root, $"started-{pid}.json"), JsonSerializer.Serialize(new { pid, startTicks, args }));

bool IsAuthenticated()
{
    if (!File.Exists(accountPath)) return true;
    using var doc = JsonDocument.Parse(File.ReadAllText(accountPath));
    return !doc.RootElement.TryGetProperty("authenticated", out var prop) || prop.GetBoolean();
}

if (args.FirstOrDefault() == "models")
{
    if (!IsAuthenticated())
    {
        await Console.Error.WriteLineAsync("Error: authentication required. Run `agy` to sign in.");
        return 1;
    }

    if (File.Exists(Path.Combine(root, "fake-models-unavailable")))
    {
        await Console.Error.WriteLineAsync("Model listing is temporarily unavailable.");
        return 2;
    }

    var modelsFile = Path.Combine(root, "fake-models.txt");
    if (File.Exists(modelsFile))
    {
        Console.WriteLine(await File.ReadAllTextAsync(modelsFile));
        return 0;
    }

    Console.WriteLine("""
        gemini-4-neon-xhigh          Gemini 4 Neon (XHigh)
        gemini-4-neon-high           Gemini 4 Neon (High)
        gemini-4-neon-medium         Gemini 4 Neon (Medium)
        gemini-4-neon-low            Gemini 4 Neon (Low)
        gemini-3.8-flash-high        Gemini 3.8 Flash (High)
        gemini-3.8-flash-medium      Gemini 3.8 Flash (Medium)
        claude-sonnet-4-6            Claude Sonnet 4.6
        gpt-oss-120b-medium          GPT-OSS 120B (Medium)
        """);
    return 0;
}

if (args.Length >= 2 && args[0] == "acp" && args.Contains("--login"))
{
    await File.WriteAllTextAsync(
        Path.Combine(root, $"login-{pid}.json"),
        JsonSerializer.Serialize(new { browser = Environment.GetEnvironmentVariable("BROWSER") }));
    var callbackMode = File.Exists(Path.Combine(root, "fake-login-callback"));
    if (callbackMode)
    {
        await Console.Error.WriteLineAsync("Visit: https://accounts.google.com/o/oauth2/auth?client=agy&mode=callback");
        await Console.Error.FlushAsync();
        await Task.Delay(150);
        await File.WriteAllTextAsync(accountPath, "{\"authenticated\":true}");
        Console.WriteLine("Sign-in complete.");
        return 0;
    }

    Console.WriteLine("Opening browser to sign in...");
    Console.WriteLine("If the browser did not open, visit: https://accounts.google.com/o/oauth2/auth?client=agy");
    Console.Out.Flush();
    while (true)
    {
        var code = await Console.In.ReadLineAsync();
        if (code is null) return 1;
        if (code.Trim() == "good-code") break;
        Console.WriteLine("Invalid code. Try again.");
        Console.Out.Flush();
    }
    Console.WriteLine("Sign-in complete.");
    await File.WriteAllTextAsync(accountPath, "{\"authenticated\":true}");
    return 0;
}

if (args.Contains("-p"))
{
    var pIdx = Array.IndexOf(args, "-p");
    var prompt = pIdx >= 0 && pIdx + 1 < args.Length ? args[pIdx + 1] : string.Empty;
    if (prompt == "/logout")
    {
        await File.WriteAllTextAsync(accountPath, "{\"authenticated\":false}");
        Console.WriteLine("{\"status\":\"SUCCESS\",\"response\":\"Signed out\"}");
        return 0;
    }

    if (prompt == "/usage")
    {
        if (!IsAuthenticated())
        {
            await Console.Error.WriteLineAsync("authentication required");
            return 1;
        }
        var usageHome = Environment.GetEnvironmentVariable("HOME") ?? "";
        var usageSettingsPath = Path.Combine(usageHome, ".gemini", "antigravity-cli", "settings.json");
        if (File.Exists(usageSettingsPath))
        {
            using var settingsDoc = JsonDocument.Parse(await File.ReadAllTextAsync(usageSettingsPath));
            if (settingsDoc.RootElement.TryGetProperty("disableSlashCommands", out var disableProp)
                && disableProp.ValueKind == JsonValueKind.True)
            {
                Console.WriteLine("{\"status\":\"SUCCESS\",\"response\":\"\",\"num_turns\":1,\"usage\":{\"total_tokens\":4738}}");
                return 0;
            }
        }
        var usagePath = Path.Combine(root, "fake-usage.json");
        if (File.Exists(usagePath))
        {
            Console.WriteLine(await File.ReadAllTextAsync(usagePath));
            return 0;
        }
        Console.WriteLine("""
            {
              "command": {
                "name": "usage",
                "data": {
                  "plan": "Google AI Pro",
                  "credits": 420,
                  "groups": [
                    {
                      "name": "Gemini Models",
                      "buckets": [
                        { "id": "gemini-5h", "window": "5h", "remaining_fraction": 0.8, "reset_time": "2099-01-01T05:00:00Z" },
                        { "id": "gemini-weekly", "window": "weekly", "remaining_fraction": 0.5, "reset_time": "2099-01-08T00:00:00Z" }
                      ]
                    },
                    {
                      "name": "Claude and GPT models",
                      "buckets": [
                        { "id": "claude-5h", "window": "5h", "remaining_fraction": 0.35, "reset_time": "2099-01-01T06:00:00Z" },
                        { "id": "claude-weekly", "window": "weekly", "remaining_fraction": 0.42, "reset_time": "2099-01-08T02:00:00Z" }
                      ]
                    }
                  ]
                }
              }
            }
            """);
        return 0;
    }
}

return args.FirstOrDefault() == "acp" ? await new FakeAcp(args, root, cwd).RunAsync() : 2;
