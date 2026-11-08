using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

// Seekable ZIP output split into GitHub-sized files without a second complete ZIP on disk.
public sealed class ReleasePartWriter : Stream
{
    readonly string prefix;
    readonly long partSize;
    FileStream current;
    int currentIndex = -1;
    long position, length;
    public readonly List<string> Parts = new List<string>();
    public ReleasePartWriter(string prefix, long partSize) { this.prefix = prefix; this.partSize = partSize; }
    public override bool CanRead { get { return false; } }
    public override bool CanSeek { get { return true; } }
    public override bool CanWrite { get { return true; } }
    public override long Length { get { return length; } }
    public override long Position { get { return position; } set { Seek(value, SeekOrigin.Begin); } }
    void SelectPart(int index)
    {
        if (currentIndex == index) return;
        if (current != null) current.Dispose();
        string path = prefix + (index + 1).ToString("D3");
        if (index == Parts.Count) Parts.Add(path);
        if (index >= Parts.Count) throw new IOException("Archive attempted to skip a part.");
        current = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        currentIndex = index;
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int index = checked((int)(position / partSize));
            long inPart = position % partSize;
            SelectPart(index);
            current.Position = inPart;
            int take = (int)Math.Min(count, partSize - inPart);
            current.Write(buffer, offset, take);
            position += take; offset += take; count -= take;
            length = Math.Max(length, position);
        }
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? position + offset : length + offset;
        if (target < 0 || target > length) throw new IOException("Invalid archive seek.");
        return position = target;
    }
    public override void Flush() { if (current != null) current.Flush(); }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    protected override void Dispose(bool disposing) { if (disposing && current != null) current.Dispose(); base.Dispose(disposing); }
}

// ZIP verification reads the parts as one seekable stream, also avoiding a second ZIP on disk.
public sealed class ReleasePartReader : Stream
{
    readonly FileStream[] files;
    readonly long[] starts;
    long position;
    public ReleasePartReader(string[] paths)
    {
        files = new FileStream[paths.Length]; starts = new long[paths.Length + 1];
        try {
            for (int i = 0; i < paths.Length; i++) { files[i] = File.OpenRead(paths[i]); starts[i + 1] = starts[i] + files[i].Length; }
        } catch { foreach (var file in files) if (file != null) file.Dispose(); throw; }
    }
    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return true; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { return starts[starts.Length - 1]; } }
    public override long Position { get { return position; } set { Seek(value, SeekOrigin.Begin); } }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (count > 0 && position < Length)
        {
            int index = 0;
            while (index + 1 < files.Length && starts[index + 1] <= position) index++;
            files[index].Position = position - starts[index];
            int take = (int)Math.Min(count, starts[index + 1] - position);
            int read = files[index].Read(buffer, offset, take);
            if (read == 0) throw new EndOfStreamException();
            position += read; offset += read; count -= read; total += read;
        }
        return total;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? position + offset : Length + offset;
        if (target < 0 || target > Length) throw new IOException("Invalid archive seek.");
        return position = target;
    }
    public override void Flush() { }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    protected override void Dispose(bool disposing) { if (disposing) foreach (var file in files) file.Dispose(); base.Dispose(disposing); }
}

public sealed class ReleaseCopyResult { public long Bytes; public string Sha256; }
public static class ReleaseTransfer
{
    public static ReleaseCopyResult Copy(Stream input, Stream output)
    {
        var result = new ReleaseCopyResult();
        byte[] buffer = new byte[8 * 1024 * 1024];
        using (var hash = SHA256.Create())
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (output != null) output.Write(buffer, 0, read);
                hash.TransformBlock(buffer, 0, read, null, 0);
                result.Bytes += read;
            }
            hash.TransformFinalBlock(new byte[0], 0, 0);
            result.Sha256 = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
        }
        return result;
    }
}
