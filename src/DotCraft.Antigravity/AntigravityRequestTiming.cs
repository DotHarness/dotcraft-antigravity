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

    public static (long? Cursor, TokenUsageRequestTiming? Timing) ReadAfter(
        string isolatedGeminiDir,
        string sessionId,
        long after,
        long inputTokens,
        long outputTokens,
        long? reasoningTokens)
    {
        var path = DatabasePath(isolatedGeminiDir, sessionId);
        if (path is null || !File.Exists(path))
            return (null, null);

        try
        {
            using var connection = new SqliteConnection(ReadOnlyConnectionString(path));
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = """
                SELECT idx, CASE WHEN length(data) <= $maxBytes THEN data END
                FROM gen_metadata WHERE idx > $after ORDER BY idx DESC LIMIT 2
                """;
            query.Parameters.AddWithValue("$after", after);
            query.Parameters.AddWithValue("$maxBytes", MaxRowBytes);
            using var rows = query.ExecuteReader();
            if (!rows.Read())
                return (null, null);

            var latest = rows.GetInt64(0);
            var data = rows.IsDBNull(1) ? null : rows.GetFieldValue<byte[]>(1);
            if (rows.Read() || data is null || outputTokens <= 0)
                return (latest, null);

            return TryMatch(data, inputTokens, outputTokens, reasoningTokens, out var timing)
                ? (latest, timing)
                : (latest, null);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or InvalidCastException)
        {
            return (null, null);
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

    private static bool TryMatch(
        ReadOnlySpan<byte> record, long inputTokens, long outputTokens, long? reasoningTokens,
        out TokenUsageRequestTiming? timing)
    {
        timing = null;
        ReadOnlySpan<byte> model = default;
        var row = new ProtoReader(record);
        while (row.Next())
            if (row is { Field: 1, Wire: 2 })
                model = row.Bytes;
        if (model.IsEmpty) return false;
        ulong input = 0, output = 0, reasoning = 0;
        double? first = null, generation = null;
        var chat = new ProtoReader(model);
        while (chat.Next())
        {
            if (chat.Wire != 2)
                continue;
            switch (chat.Field)
            {
                case 4:
                    var usage = new ProtoReader(chat.Bytes);
                    while (usage.Next())
                    {
                        if (usage.Wire != 0)
                            continue;
                        if (usage.Field == 2) input = usage.Number;
                        if (usage.Field == 3) output = usage.Number;
                        if (usage.Field == 9) reasoning = usage.Number;
                    }
                    break;
                case 11:
                    first = Milliseconds(chat.Bytes);
                    break;
                case 12:
                    generation = Milliseconds(chat.Bytes);
                    break;
            }
        }

        if (input != (ulong)inputTokens || output != (ulong)outputTokens
            || (reasoningTokens is long reason && reasoning != (ulong)reason))
            return false;

        if (first is double firstMs && generation is > 0)
            timing = new(firstMs, generation.Value);
        return true;
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
