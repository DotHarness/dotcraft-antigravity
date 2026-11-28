using System.Text;
using DotHarness.Acp.Transport;

namespace DotCraft.Antigravity;

internal sealed class AntigravityAcpTransport(AntigravityProcess process) : IAcpTransport
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] Newline = [(byte)'\n'];

    public async ValueTask<ReadOnlyMemory<byte>?> ReadMessageAsync(CancellationToken ct = default)
    {
        while (await process.Output.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.AsSpan().TrimStart() is not ['{', ..])
                continue;
            return Utf8NoBom.GetBytes(line);
        }

        return null;
    }

    public async ValueTask WriteMessageAsync(ReadOnlyMemory<byte> message, CancellationToken ct = default)
    {
        var stream = process.Input.BaseStream;
        await stream.WriteAsync(message, ct).ConfigureAwait(false);
        await stream.WriteAsync(Newline, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => process.DisposeAsync();
}
