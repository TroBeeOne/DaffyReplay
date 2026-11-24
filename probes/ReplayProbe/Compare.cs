using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReplayProbe;

/// <summary>
/// Byte-level fidelity: a recording, and DaffyTee's recording of its replay, compared request by
/// request. Every request payload must be in both, byte for byte, apart from the two fields a
/// replay changes on purpose: the transaction descriptor, which the replay server hands out
/// itself, and the outstanding-request count, which is 1 because even a MARS session's requests
/// are replayed one at a time.
/// Compared as multisets, so sessions interleaving differently on replay does not matter.
/// </summary>
internal static class Compare
{
    public static int Run(string recordingPath, string capturePath)
    {
        var (recorded, recordedResets) = Read(recordingPath);
        var (replayed, replayedResets) = Read(capturePath);

        long missing = 0, extra = 0;
        foreach (var (hash, (count, sample)) in recorded)
        {
            long other = replayed.TryGetValue(hash, out var r) ? r.Count : 0;
            if (other < count)
            {
                missing += count - other;
                if (missing <= 5) Console.WriteLine($"  missing from the replay ({count - other}x): {sample}");
            }
        }
        foreach (var (hash, (count, sample)) in replayed)
        {
            long other = recorded.TryGetValue(hash, out var r) ? r.Count : 0;
            if (other < count)
            {
                extra += count - other;
                if (extra <= 5) Console.WriteLine($"  not in the recording ({count - other}x): {sample}");
            }
        }

        long total = recorded.Values.Sum(v => v.Count);
        Console.WriteLine($"  {total} recorded request payloads, {replayed.Values.Sum(v => v.Count)} replayed; " +
                          $"{missing} missing, {extra} extra; resets {recordedResets} recorded, {replayedResets} replayed");
        return missing == 0 && extra == 0 && recordedResets == replayedResets ? 0 : 1;
    }

    private static (Dictionary<string, (long Count, string Sample)> Payloads, long Resets) Read(string path)
    {
        var payloads = new Dictionary<string, (long Count, string Sample)>();
        long resets = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.GetProperty("type").GetString()!.StartsWith("request.")) continue;
            var request = root.GetProperty("data").GetProperty("request");
            if (request.TryGetProperty("gatewayCommand", out _)) continue;
            if (request.TryGetProperty("resetConnection", out var reset) && reset.GetBoolean()) resets++;
            if (!request.TryGetProperty("raw", out var raw)) continue;

            byte[] bytes = raw.GetBytesFromBase64();
            string kind = request.GetProperty("kind").GetString()!;
            if (kind != "bulk") ZeroDescriptor(bytes);

            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            string sample = $"{kind} {(request.TryGetProperty("sql", out var sql) ? sql.GetString() : request.TryGetProperty("calls", out var calls) ? calls[0].GetProperty("proc").GetString() : "")}";
            var existing = payloads.GetValueOrDefault(hash);
            payloads[hash] = (existing.Count + 1, existing.Sample ?? (sample.Length > 100 ? sample[..100] : sample));
        }
        return (payloads, resets);
    }

    /// <summary>ALL_HEADERS: total length, then headers; type 2 is the transaction descriptor and its count.</summary>
    private static void ZeroDescriptor(byte[] payload)
    {
        if (payload.Length < 4) return;
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (total < 4 || total > 1024 || total > payload.Length) return;
        int position = 4;
        while (position + 6 <= total)
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(position));
            if (length < 6 || position + length > total) return;
            if (BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(position + 4)) == 2 && length >= 14)
                payload.AsSpan(position + 6, Math.Min(12, (int)length - 6)).Clear();   // descriptor and count
            position += (int)length;
        }
    }
}
