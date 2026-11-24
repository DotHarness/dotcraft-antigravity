using DotCraft.Tracing;
using Microsoft.Data.Sqlite;

namespace DotCraft.Antigravity;

internal static class AntigravityRequestTiming
{
    private const int RecentRows = 8;
    private const int MaxRowBytes = 2 * 1024 * 1024;

    public static TokenUsageRequestTiming? Read(
        string isolatedGeminiDir,
        string? conversationId,
        int stepIndex,
        long outputTokens,
        long? reasoningTokens)
    {
        if (string.IsNullOrEmpty(isolatedGeminiDir)
            || !Guid.TryParseExact(conversationId, "D", out var id)
            || stepIndex < 0
            || outputTokens <= 0)
        {
            return null;
        }

        var path = Path.Combine(isolatedGeminiDir, "conversations", $"{id:D}.db");
        if (!File.Exists(path))
            return null;

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 1
            }.ToString());
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = """
                SELECT CASE WHEN length(data) <= $maxBytes THEN data END
                FROM gen_metadata ORDER BY idx DESC LIMIT $recentRows
                """;
            query.Parameters.AddWithValue("$maxBytes", MaxRowBytes);
            query.Parameters.AddWithValue("$recentRows", RecentRows);
            using var rows = query.ExecuteReader();
            var found = false;
            TokenUsageRequestTiming? timing = null;
            while (rows.Read())
            {
                if (rows.IsDBNull(0))
                    return null;
                if (!TryMatch(rows.GetFieldValue<byte[]>(0), stepIndex, outputTokens, reasoningTokens, out var candidate))
                    continue;
                if (found)
                    return null;
                found = true;
                timing = candidate;
            }
            return timing;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or InvalidCastException)
        {
            return null;
        }
    }

    // AGY stores CortexStepGeneratorMetadata protobufs alongside the steps they produced.
    private static bool TryMatch(
        ReadOnlySpan<byte> record,
        int stepIndex,
        long outputTokens,
        long? reasoningTokens,
        out TokenUsageRequestTiming? timing)
    {
        timing = null;
        ReadOnlySpan<byte> model = default;
        var stepCount = 0;
        var matchedStep = false;
        var row = new ProtoReader(record);
        while (row.Next())
        {
            switch (row.Field, row.Wire)
            {
                case (1, 2):
                    model = row.Bytes;
                    break;
                case (2, 0):
                    stepCount++;
                    matchedStep |= row.Number == (ulong)stepIndex;
                    break;
                case (2, 2):
                    var indices = new ProtoReader(row.Bytes);
                    while (!indices.Done)
                    {
                        stepCount++;
                        matchedStep |= indices.Varint() == (ulong)stepIndex;
                    }
                    break;
            }
        }

        if (!matchedStep || stepCount != 1 || model.IsEmpty)
            return false;

        ulong output = 0, reasoning = 0;
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

        if (output != (ulong)outputTokens || (reasoningTokens is long reason && reasoning != (ulong)reason))
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
