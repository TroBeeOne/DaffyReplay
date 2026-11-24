using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace DaffyReplay.Recording;

/// <summary>One line of a recording file: where it is, and (only during the call that receives it) its bytes.</summary>
internal readonly ref struct RecordingLine(int file, long offset, ReadOnlySpan<byte> bytes)
{
    public int File { get; } = file;
    public long Offset { get; } = offset;
    public ReadOnlySpan<byte> Bytes { get; } = bytes;
}

internal delegate void LineHandler(in RecordingLine line);

/// <summary>
/// The event files of one recording - DaffyTee's file sink rolls them at midnight, so a recording
/// of any length is usually several - streamed, never loaded whole.
///
/// The index pass reads every line once, in file order, with its offset. The replay then reads
/// each event by offset as it reaches it, with positional reads that any number of sessions can
/// make at once without a lock.
/// </summary>
internal sealed class RecordingFiles : IDisposable
{
    private readonly SafeFileHandle[] _handles;

    public IReadOnlyList<string> Paths { get; }

    public long TotalBytes { get; }

    private RecordingFiles(IReadOnlyList<string> paths)
    {
        Paths = paths;
        _handles = paths.Select(p => File.OpenHandle(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess)).ToArray();
        TotalBytes = _handles.Sum(RandomAccess.GetLength);
    }

    /// <summary>
    /// Opens the files named, expanding wildcards in the file name (D:\capture\events-202610*.ndjson)
    /// in name order - which, for DaffyTee's {date} names, is time order.
    /// </summary>
    public static RecordingFiles Open(IEnumerable<string> patterns)
    {
        var paths = new List<string>();
        foreach (string pattern in patterns)
        {
            string full = Path.GetFullPath(pattern);
            string name = Path.GetFileName(full);
            if (name.IndexOfAny(['*', '?']) < 0)
            {
                if (!File.Exists(full)) throw new ArgumentException($"no such recording file: {pattern}");
                paths.Add(full);
                continue;
            }

            string directory = Path.GetDirectoryName(full) ?? ".";
            var matches = Directory.Exists(directory)
                ? Directory.GetFiles(directory, name).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            if (matches.Count == 0) throw new ArgumentException($"no recording files match {pattern}");
            paths.AddRange(matches);
        }

        if (paths.Count == 0) throw new ArgumentException("name at least one recording file (NDJSON from DaffyTee's file sink)");
        if (paths.Count > short.MaxValue) throw new ArgumentException("too many recording files");
        return new RecordingFiles(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// Every non-empty line of every file, in order, to <paramref name="handler"/>. A line longer than
    /// the buffer grows it: an event with its raw bytes can be several megabytes.
    /// </summary>
    public void ReadLines(LineHandler handler, Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        long done = 0;
        try
        {
            for (int file = 0; file < _handles.Length; file++)
            {
                var handle = _handles[file];
                long position = 0, lineStart = 0;
                int filled = 0;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (filled == buffer.Length)
                    {
                        var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                        Buffer.BlockCopy(buffer, 0, bigger, 0, filled);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = bigger;
                    }

                    int read = RandomAccess.Read(handle, buffer.AsSpan(filled), position);
                    position += read;
                    done += read;
                    bool end = read == 0;
                    filled += read;

                    int scan = 0;
                    while (true)
                    {
                        int newline = buffer.AsSpan(scan, filled - scan).IndexOf((byte)'\n');
                        if (newline < 0) break;
                        Deliver(handler, file, lineStart, buffer.AsSpan(scan, newline));
                        scan += newline + 1;
                        lineStart += newline + 1;
                    }

                    if (end)
                    {
                        if (scan < filled) Deliver(handler, file, lineStart, buffer.AsSpan(scan, filled - scan));
                        break;
                    }

                    // Keep the unfinished line at the front for the next read.
                    if (scan > 0)
                    {
                        Buffer.BlockCopy(buffer, scan, buffer, 0, filled - scan);
                        filled -= scan;
                    }
                    progress?.Invoke(done);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void Deliver(LineHandler handler, int file, long offset, ReadOnlySpan<byte> line)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
        if (line.Length > 0 && line[0] == 0xEF && line.Length >= 3 && line[1] == 0xBB && line[2] == 0xBF)
        {
            line = line[3..];
            offset += 3;
        }
        if (line.IsEmpty || line.IndexOfAnyExcept((byte)' ', (byte)'\t') < 0) return;
        handler(new RecordingLine(file, offset, line));
    }

    /// <summary>One event's bytes, by where the index pass found it.</summary>
    public byte[] ReadAt(int file, long offset, int length)
    {
        byte[] bytes = new byte[length];
        int done = 0;
        while (done < length)
        {
            int read = RandomAccess.Read(_handles[file], bytes.AsSpan(done), offset + done);
            if (read == 0) throw new IOException($"{Paths[file]} is shorter than when it was indexed");
            done += read;
        }
        return bytes;
    }

    public void Dispose()
    {
        foreach (var handle in _handles) handle.Dispose();
    }
}
