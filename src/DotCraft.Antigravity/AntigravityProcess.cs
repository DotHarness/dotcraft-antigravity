using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace DotCraft.Antigravity;

internal sealed class AntigravityProcess : IAsyncDisposable
{
    private const int MaxStderrChars = 64 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly Task _stderrTask;
    private readonly object _stderrLock = new();
    private readonly StringBuilder _stderr = new();
    private int _disposed;

    public StreamReader Output => _process.StandardOutput;
    public StreamWriter Input => _process.StandardInput;
    public bool HasExited => _process.HasExited;

    public AntigravityProcess(
        string executablePath,
        string workingDirectory,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in arguments)
            start.ArgumentList.Add(arg);

        if (environment is not null)
        {
            start.Environment.Clear();
            foreach (var (key, value) in environment)
                start.Environment[key] = value;
        }

        try
        {
            _process = Process.Start(start) ?? throw new IOException("Antigravity.StartFailed: Could not start the Antigravity CLI executable.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Antigravity.CliNotFound: Could not launch '{executablePath}'. Install the official Antigravity CLI (agy) or configure executablePath in plugin settings.",
                ex);
        }

        _stderrTask = DrainStderrAsync();
    }

    public string StderrTail
    {
        get
        {
            lock (_stderrLock) return _stderr.ToString();
        }
    }

    public async Task WriteJsonLineAsync(JsonObject message, CancellationToken ct)
    {
        var line = message.ToJsonString(AntigravityJson.Compact);
        await Input.WriteLineAsync(line.AsMemory(), ct);
        await Input.FlushAsync(ct);
    }

    public async Task<int> ExitAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct);
        await _stderrTask;
        return _process.ExitCode;
    }

    public async Task<(int ExitCode, string Stdout, string Stderr)> ReadToExitAsync(CancellationToken ct)
    {
        var stdout = await Output.ReadToEndAsync(ct);
        await _process.WaitForExitAsync(ct);
        await _stderrTask;
        return (_process.ExitCode, stdout, StderrTail);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await _process.WaitForExitAsync();
            await _stderrTask;
        }
        catch
        {
        }
        finally
        {
            _process.Dispose();
        }
    }

    private async Task DrainStderrAsync()
    {
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await _process.StandardError.ReadAsync(buffer)) > 0)
            {
                lock (_stderrLock)
                {
                    _stderr.Append(buffer, 0, read);
                    if (_stderr.Length > MaxStderrChars)
                        _stderr.Remove(0, _stderr.Length - MaxStderrChars);
                }
            }
        }
        catch
        {
        }
    }
}
