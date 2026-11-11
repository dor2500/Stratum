namespace Stratum.Core;

/// <summary>
/// Reads name.001, name.002, ... as one continuous seekable stream.
/// This is the generic split layout 7-Zip uses for every format.
/// </summary>
public sealed class SplitVolumeStream : Stream
{
    private readonly string[] _paths;
    private readonly long[] _starts;
    private readonly long _length;
    private FileStream? _current;
    private int _currentIndex = -1;
    private long _position;

    public SplitVolumeStream(string firstVolumePath)
    {
        _paths = [.. FindVolumes(firstVolumePath)];
        if (_paths.Length == 0) throw new FileNotFoundException("Volume not found.", firstVolumePath);
        _starts = new long[_paths.Length];
        long offset = 0;
        for (var i = 0; i < _paths.Length; i++)
        {
            _starts[i] = offset;
            offset += new FileInfo(_paths[i]).Length;
        }
        _length = offset;
    }

    public int VolumeCount => _paths.Length;

    public static bool IsFirstVolume(string path) => path.EndsWith(".001", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for name.002 and later, which cannot be opened on their own.</summary>
    public static bool IsLaterVolume(string path, out string firstVolume)
    {
        firstVolume = path;
        var ext = Path.GetExtension(path);
        if (ext.Length != 4 || !int.TryParse(ext.AsSpan(1), out var n) || n < 2) return false;
        firstVolume = path[..^3] + "001";
        return File.Exists(firstVolume);
    }

    public static IReadOnlyList<string> FindVolumes(string firstVolumePath)
    {
        var stem = firstVolumePath[..^3];
        var list = new List<string>();
        for (var i = 1; i < 1000; i++)
        {
            var p = $"{stem}{i:000}";
            if (!File.Exists(p)) break;
            list.Add(p);
        }
        return list;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (count > 0 && _position < _length)
        {
            var index = Array.BinarySearch(_starts, _position);
            if (index < 0) index = ~index - 1;

            if (index != _currentIndex)
            {
                _current?.Dispose();
                _current = new FileStream(_paths[index], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                _currentIndex = index;
            }

            _current!.Position = _position - _starts[index];
            var read = _current.Read(buffer, offset, count);
            if (read == 0) break;
            total += read;
            offset += read;
            count -= read;
            _position += read;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _current?.Dispose();
        base.Dispose(disposing);
    }
}

public static class VolumeSplitter
{
    /// <summary>Splits a finished archive into path.001, path.002, ... and removes the original.</summary>
    public static IReadOnlyList<string> Split(string path, long volumeSize, CancellationToken ct = default)
    {
        if (volumeSize <= 0) throw new ArgumentOutOfRangeException(nameof(volumeSize));
        var created = new List<string>();
        try
        {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var buffer = new byte[1 << 20];
                for (var number = 1; source.Position < source.Length; number++)
                {
                    if (number > 999) throw new ArchiveException("The volume size is too small: more than 999 volumes would be needed.");
                    var partPath = $"{path}.{number:000}";
                    created.Add(partPath);
                    using var part = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                    var remaining = volumeSize;
                    while (remaining > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read == 0) break;
                        part.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
            }
            File.Delete(path);
            return created;
        }
        catch
        {
            foreach (var p in created) TryDelete(p);
            throw;
        }
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
