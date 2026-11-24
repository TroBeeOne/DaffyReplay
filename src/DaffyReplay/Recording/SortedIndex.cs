using System.Runtime.InteropServices;

namespace DaffyReplay.Recording;

internal enum EntryKind : byte { SessionStart = 0, Request = 1, SessionEnd = 2 }

/// <summary>
/// One replayable thing in start order: a session opening, a request, a session closing. Where the
/// event is (file, offset, length), not the event itself - about 40 bytes, whatever the event's size.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly record struct IndexEntry(long Ticks, long Offset, long Seq, int Session, int Length, short File, EntryKind Kind)
{
    public static int Compare(in IndexEntry a, in IndexEntry b)
    {
        int c = a.Ticks.CompareTo(b.Ticks);
        if (c != 0) return c;
        c = a.Kind.CompareTo(b.Kind);
        if (c != 0) return c;
        c = a.Session.CompareTo(b.Session);
        return c != 0 ? c : a.Seq.CompareTo(b.Seq);
    }
}

/// <summary>
/// The index entries, sorted by start time. Small recordings sort in memory; past
/// <see cref="ChunkEntries"/> each full chunk is sorted and spilled to a temporary file, and the
/// chunks are merged as the replay reads them - so a recording of any size is replayed in a
/// bounded amount of memory.
/// </summary>
internal sealed class SortedIndex : IDisposable
{
    public const int DefaultChunkEntries = 4_000_000;   // about 160 MB of entries in memory at most

    private readonly int _chunkEntries;
    private readonly List<string> _spilled = [];
    private List<IndexEntry> _current = [];
    private string? _directory;

    public SortedIndex(int chunkEntries = DefaultChunkEntries) => _chunkEntries = chunkEntries;

    public long Count { get; private set; }

    /// <summary>Chunks written to disk; zero for a recording that fit in memory.</summary>
    public int SpilledChunks => _spilled.Count;

    public void Add(in IndexEntry entry)
    {
        _current.Add(entry);
        Count++;
        if (_current.Count >= _chunkEntries) Spill();
    }

    /// <summary>Every entry, in order. Can be enumerated more than once.</summary>
    public IEnumerable<IndexEntry> InOrder()
    {
        SortCurrent();
        return Merge();
    }

    private void SortCurrent() => CollectionsMarshal.AsSpan(_current).Sort(static (a, b) => IndexEntry.Compare(a, b));

    private IEnumerable<IndexEntry> Merge()
    {
        if (_spilled.Count == 0)
        {
            for (int i = 0; i < _current.Count; i++) yield return _current[i];
            yield break;
        }

        var readers = _spilled.Select(path => new ChunkReader(path)).ToList();
        try
        {
            var queue = new PriorityQueue<int, IndexEntry>(Comparer<IndexEntry>.Create(static (a, b) => IndexEntry.Compare(a, b)));
            for (int i = 0; i < readers.Count; i++)
                if (readers[i].TryRead(out var first)) queue.Enqueue(i, first);

            int memory = 0;
            const int InMemory = -1;
            if (_current.Count > 0) queue.Enqueue(InMemory, _current[0]);

            while (queue.TryDequeue(out int source, out var entry))
            {
                yield return entry;
                if (source == InMemory)
                {
                    if (++memory < _current.Count) queue.Enqueue(InMemory, _current[memory]);
                }
                else if (readers[source].TryRead(out var next))
                {
                    queue.Enqueue(source, next);
                }
            }
        }
        finally
        {
            foreach (var reader in readers) reader.Dispose();
        }
    }

    private void Spill()
    {
        SortCurrent();
        var span = CollectionsMarshal.AsSpan(_current);

        _directory ??= Directory.CreateTempSubdirectory("daffyreplay-index-").FullName;
        string path = Path.Combine(_directory, $"chunk{_spilled.Count:D4}.bin");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            stream.Write(MemoryMarshal.AsBytes(span));
        _spilled.Add(path);
        _current = new List<IndexEntry>(_chunkEntries);
    }

    public void Dispose()
    {
        if (_directory is null) return;
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class ChunkReader(string path) : IDisposable
    {
        private readonly FileStream _stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        private readonly byte[] _entry = new byte[Marshal.SizeOf<IndexEntry>()];

        public bool TryRead(out IndexEntry entry)
        {
            int filled = 0;
            while (filled < _entry.Length)
            {
                int read = _stream.Read(_entry, filled, _entry.Length - filled);
                if (read == 0)
                {
                    entry = default;
                    return false;
                }
                filled += read;
            }
            entry = MemoryMarshal.Read<IndexEntry>(_entry);
            return true;
        }

        public void Dispose() => _stream.Dispose();
    }
}
