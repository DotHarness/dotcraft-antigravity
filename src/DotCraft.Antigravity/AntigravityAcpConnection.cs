using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotCraft.Antigravity;

internal sealed class AntigravityAcpException(int code, string message) : IOException(message)
{
    public int Code { get; } = code;
}

internal sealed class AntigravityAcpConnection : IAsyncDisposable
{
    private readonly AntigravityProcess _process;
    private readonly Func<JsonObject, Task> _onMessage;
    private readonly CancellationTokenSource _stopping;
    private readonly CancellationToken _lifetime;
    private readonly SemaphoreSlim _writes = new(1);
    private readonly Dictionary<long, TaskCompletionSource<JsonObject>> _pending = [];
    private Exception? _closed;
    private long _nextId;
    private int _disposed;

    public Task Completion { get; }

    public AntigravityAcpConnection(
        AntigravityProcess process, Func<JsonObject, Task> onMessage, CancellationToken stopping)
    {
        _process = process;
        _onMessage = onMessage;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _lifetime = _stopping.Token;
        Completion = ReadAsync();
    }

    public async Task<JsonObject> SendRequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var response = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            ThrowIfClosed();
            _pending.Add(id, response);
        }

        try
        {
            var message = MethodMessage(method, parameters);
            message["id"] = id;
            await WriteAsync(message, ct);
            return await response.Task.WaitAsync(ct);
        }
        finally
        {
            lock (_pending)
                _pending.Remove(id);
        }
    }

    public Task SendNotificationAsync(string method, JsonObject parameters, CancellationToken ct) =>
        WriteAsync(MethodMessage(method, parameters), ct);

    public Task RespondAsync(JsonNode id, JsonObject result, CancellationToken ct) =>
        WriteAsync(new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result.DeepClone() }, ct);

    public Task RespondErrorAsync(JsonNode id, int code, string message, CancellationToken ct) =>
        WriteAsync(new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        }, ct);

    private static JsonObject MethodMessage(string method, JsonObject parameters) =>
        new() { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters.DeepClone() };

    private async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        await _writes.WaitAsync(linked.Token);
        try
        {
            lock (_pending)
                ThrowIfClosed();
            linked.Token.ThrowIfCancellationRequested();
            // A caller can abandon a request, but cannot leave half a JSON frame on the shared pipe.
            await _process.WriteJsonLineAsync(message, _lifetime);
        }
        finally
        {
            _writes.Release();
        }
    }

    private async Task ReadAsync()
    {
        Exception? failure = null;
        try
        {
            while (await _process.Output.ReadLineAsync(_lifetime) is { } line)
            {
                if (line.AsSpan().TrimStart() is not ['{', ..]) continue;
                JsonObject message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject
                        ?? throw new JsonException("Expected a JSON object.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("Antigravity.AcpProtocol: The ACP worker returned invalid JSON.", ex);
                }

                if (message["method"] is JsonValue)
                    await _onMessage(message);
                else if (message["id"] is JsonValue id && id.TryGetValue<long>(out var number))
                    CompleteRequest(number, message);
            }

            _lifetime.ThrowIfCancellationRequested();
            throw new IOException("Antigravity.AcpExited: The Antigravity ACP worker closed its output.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            lock (_pending)
            {
                _closed = failure ?? new OperationCanceledException(_lifetime);
                foreach (var response in _pending.Values)
                {
                    if (failure is null)
                        response.TrySetCanceled(_lifetime);
                    else
                        response.TrySetException(failure);
                }
                _pending.Clear();
            }
        }
    }

    private void CompleteRequest(long id, JsonObject message)
    {
        TaskCompletionSource<JsonObject>? response;
        lock (_pending)
        {
            if (!_pending.Remove(id, out response)) return;
        }

        if (message["error"] is JsonObject error
            && error["code"] is JsonValue code && code.TryGetValue<int>(out var number)
            && error["message"] is JsonValue reason && reason.TryGetValue<string>(out var description))
        {
            response.TrySetException(new AntigravityAcpException(number, description));
        }
        else if (message["result"] is JsonObject result)
        {
            response.TrySetResult(result);
        }
        else if (message.ContainsKey("result") && message["result"] is null)
        {
            response.TrySetResult(new());
        }
        else
        {
            response.TrySetException(new InvalidDataException("Antigravity.AcpProtocol: The ACP worker returned an invalid response."));
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed is not null) ExceptionDispatchInfo.Throw(_closed);
        _lifetime.ThrowIfCancellationRequested();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync();
        try
        {
            await _process.DisposeAsync();
            await Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            _stopping.Dispose();
        }
    }
}
