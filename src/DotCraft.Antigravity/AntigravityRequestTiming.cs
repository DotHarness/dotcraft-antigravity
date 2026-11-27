using DotCraft.Tracing;
using Microsoft.Data.Sqlite;

namespace DotCraft.Antigravity;

internal static class AntigravityRequestTiming
{
    private const int MaxRowBytes = 2 * 1024 * 1024;

    public static long? ReadCursor(string isolatedGeminiDir, string sessionId)
    {
        var path = DatabasePath(isolatedGeminiDir, sessionId);
        if (path is null)
            return null;
        if (!File.Exists(path))
            return -1;

        try
        {
            using var connection = new SqliteConnection(ReadOnlyConnectionString(path));
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT coalesce(max(idx), -1) FROM gen_metadata";
            return query.ExecuteScalar() as long?;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public static GenerationBatch? ReadAfter(string isolatedGeminiDir, string sessionId, long after)
    {
        var path = DatabasePath(isolatedGeminiDir, sessionId);
        if (path is null || !File.Exists(path))
            return null;

        try
        {
            using var connection = new SqliteConnection(ReadOnlyConnectionString(path));
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = """
                SELECT idx, CASE WHEN length(data) <= $maxBytes THEN data END
                FROM gen_metadata WHERE idx > $after ORDER BY idx
                """;
            query.Parameters.AddWithValue("$after", after);
            query.Parameters.AddWithValue("$maxBytes", MaxRowBytes);
            using var rows = query.ExecuteReader();
            var count = 0;
            var cursor = after;
            var usage = new GenerationUsage();
            var timed = true;
            double firstMs = 0, generationMs = 0;
            while (rows.Read())
            {
                if (rows.IsDBNull(1) || ReadGeneration(rows.GetFieldValue<byte[]>(1)) is not { } record)
                    return null;
                cursor = rows.GetInt64(0);
                usage += record.Usage;
                count++;
                if (record.Timing is { } timing)
                {
                    firstMs += timing.FirstOutputLatencyMs;
                    generationMs += timing.GenerationMs;
                }
                else
                {
                    timed = false;
                }
            }

            return count == 0 ? null : new(cursor, usage,
                timed ? new(firstMs / count, generationMs) : null);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static string? DatabasePath(string isolatedGeminiDir, string? conversationId) =>
        !string.IsNullOrEmpty(isolatedGeminiDir) && Guid.TryParseExact(conversationId, "D", out var id)
            ? Path.Combine(isolatedGeminiDir, "conversations", $"{id:D}.db")
            : null;

    private static string ReadOnlyConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
        DefaultTimeout = 1
    }.ToString();

    private static (GenerationUsage Usage, TokenUsageRequestTiming? Timing)? ReadGeneration(ReadOnlySpan<byte> record)
    {
        ReadOnlySpan<byte> model = default;
        var row = new ProtoReader(record);
        while (row.Next())
            if (row is { Field: 1, Wire: 2 })
                model = row.Bytes;
        if (model.IsEmpty) return null;
        GenerationUsage? usage = null;
        double? first = null, generation = null;
        var chat = new ProtoReader(model);
        while (chat.Next())
        {
            if (chat.Wire != 2)
                continue;
            switch (chat.Field)
            {
                case 4:
                    usage = ReadUsage(chat.Bytes);
                    break;
                case 11:
                    first = Milliseconds(chat.Bytes);
                    break;
                case 12:
                    generation = Milliseconds(chat.Bytes);
                    break;
            }
        }

        return usage is { HasBillableTokens: true } counts
            ? (counts, first is double firstMs && generation is > 0 ? new(firstMs, generation.Value) : null)
            : null;
    }

    private static GenerationUsage ReadUsage(ReadOnlySpan<byte> payload)
    {
        long input = 0, output = 0, cachedRead = 0, cachedWrite = 0, reasoning = 0;
        var usage = new ProtoReader(payload);
        while (usage.Next())
        {
            if (usage.Wire != 0)
                continue;
            switch (usage.Field)
            {
                case 2: input = checked((long)usage.Number); break;
                case 3: output = checked((long)usage.Number); break;
                case 4: cachedWrite = checked((long)usage.Number); break;
                case 5: cachedRead = checked((long)usage.Number); break;
                case 9: reasoning = checked((long)usage.Number); break;
            }
        }

        return new(input, output, cachedRead, Math.Min(cachedWrite, input), Math.Min(reasoning, output));
    }

    internal sealed record GenerationBatch(long Cursor, GenerationUsage Usage, TokenUsageRequestTiming? Timing);

    internal readonly record struct GenerationUsage(long Input, long Output, long CachedRead, long CachedWrite, long Reasoning)
    {
        public bool HasBillableTokens => Input > 0 || Output > 0 || CachedRead > 0;

        public static GenerationUsage operator +(GenerationUsage left, GenerationUsage right) =>
            checked(new(left.Input + right.Input, left.Output + right.Output, left.CachedRead + right.CachedRead,
                left.CachedWrite + right.CachedWrite, left.Reasoning + right.Reasoning));

        public GenerationUsage RemainingAfter(GenerationUsage alreadyReported) =>
            new(Math.Max(0, Input - alreadyReported.Input), Math.Max(0, Output - alreadyReported.Output),
                Math.Max(0, CachedRead - alreadyReported.CachedRead), Math.Max(0, CachedWrite - alreadyReported.CachedWrite),
                Math.Max(0, Reasoning - alreadyReported.Reasoning));
    }

    private static double? Milliseconds(ReadOnlySpan<byte> data)
    {
        ulong seconds = 0, nanos = 0;
        var duration = new ProtoReader(data);
        while (duration.Next())
        {
            if (duration.Wire != 0)
                continue;
            if (duration.Field == 1) seconds = duration.Number;
            if (duration.Field == 2) nanos = duration.Number;
        }
        return seconds <= 315_576_000_000 && nanos < 1_000_000_000
            ? seconds * 1000d + nanos / 1_000_000d
            : null;
    }

    private ref struct ProtoReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _remaining = data;
        public bool Done => _remaining.IsEmpty;
        public int Field { get; private set; }
        public int Wire { get; private set; }
        public ulong Number { get; private set; }
        public ReadOnlySpan<byte> Bytes { get; private set; }

        public bool Next()
        {
            if (Done)
                return false;
            var tag = Varint();
            if (tag > uint.MaxValue || tag < 8)
                throw new InvalidDataException();
            Field = (int)(tag >> 3);
            Wire = (int)(tag & 7);

            switch (Wire)
            {
                case 0: Number = Varint(); break;
                case 1: Take(8); break;
                case 2: Bytes = Take(Varint()); break;
                case 5: Take(4); break;
                default: throw new InvalidDataException();
            }
            return true;
        }

        private ReadOnlySpan<byte> Take(ulong length)
        {
            if (length > (ulong)_remaining.Length)
                throw new InvalidDataException();
            var result = _remaining[..(int)length];
            _remaining = _remaining[(int)length..];
            return result;
        }

        public ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift <= 63 && !Done; shift += 7)
            {
                var next = _remaining[0];
                _remaining = _remaining[1..];
                if (shift == 63 && next > 1)
                    break;
                value |= (ulong)(next & 127) << shift;
                if (next < 128)
                    return value;
            }
            throw new InvalidDataException();
        }
    }
}
