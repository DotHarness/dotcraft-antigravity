using System.Diagnostics;
using System.Text;

namespace DotCraft.Antigravity;

internal sealed class AntigravityProcess : IAsyncDisposable
{
    public const string PidFileName = "worker.pid";
    private const int MaxStderrChars = 64 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly object TrackedLock = new();
    private static readonly HashSet<Process> TrackedProcesses = [];

    private readonly Process _process;
    private readonly Task _stderrTask;
    private readonly Action<string>? _onStderrLine;
    private readonly string? _pidFilePath;
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
        IReadOnlyDictionary<string, string>? environment = null,
        Action<string>? onStderrLine = null,
        string? pidFileDirectory = null)
    {
        _onStderrLine = onStderrLine;
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

        lock (TrackedLock) TrackedProcesses.Add(_process);
        if (!string.IsNullOrWhiteSpace(pidFileDirectory))
        {
            _pidFilePath = Path.Combine(pidFileDirectory, PidFileName);
            TryWritePidFile(_pidFilePath, _process);
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
            lock (TrackedLock) TrackedProcesses.Remove(_process);
            if (_pidFilePath is not null)
            {
                try { if (File.Exists(_pidFilePath)) File.Delete(_pidFilePath); }
                catch { }
            }
            _process.Dispose();
        }
    }

    public static void TryReapRecordedProcess(string directoryPath)
    {
        var pidFile = Path.Combine(directoryPath, PidFileName);
        if (!File.Exists(pidFile))
            return;

        try
        {
            var parts = File.ReadAllText(pidFile).Trim().Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var pid) || !long.TryParse(parts[1], out var startTicks))
                return;

            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited)
                return;
            if (Math.Abs(proc.StartTime.ToUniversalTime().Ticks - startTicks) > TimeSpan.FromSeconds(2).Ticks)
                return;

            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(2000);
        }
        catch
        {
        }
    }

    private static void TryWritePidFile(string pidFilePath, Process process)
    {
        try
        {
            var content = $"{process.Id}:{process.StartTime.ToUniversalTime().Ticks}";
            File.WriteAllText(pidFilePath, content, Utf8NoBom);
        }
        catch
        {
        }
    }

    public static void TerminateTrackedProcesses()
    {
        Process[] snapshot;
        lock (TrackedLock)
        {
            snapshot = [.. TrackedProcesses];
            TrackedProcesses.Clear();
        }

        foreach (var proc in snapshot)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
    }

    private async Task DrainStderrAsync()
    {
        var buffer = new char[1024];
        var lineBuffer = _onStderrLine is not null ? new StringBuilder() : null;
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

                if (lineBuffer is not null)
                {
                    for (var i = 0; i < read; i++)
                    {
                        var ch = buffer[i];
                        if (ch == '\n')
                        {
                            _onStderrLine!(lineBuffer.ToString().TrimEnd('\r'));
                            lineBuffer.Clear();
                        }
                        else
                        {
                            lineBuffer.Append(ch);
                        }
                    }
                }
            }

            if (lineBuffer is { Length: > 0 })
                _onStderrLine!(lineBuffer.ToString().TrimEnd('\r'));
        }
        catch
        {
        }
    }
}
