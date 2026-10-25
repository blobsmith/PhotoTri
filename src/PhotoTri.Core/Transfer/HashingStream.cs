using System.Security.Cryptography;

namespace PhotoTri.Core.Transfer;

/// <summary>
/// Flux d'écriture qui calcule l'empreinte SHA-256 de tout ce qui le traverse,
/// et signale le nombre d'octets écrits à <paramref name="onWritten"/>.
/// </summary>
sealed class HashingStream(Stream inner, Action<long>? onWritten = null) : Stream
{
    readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long BytesWritten { get; private set; }

    public string HashHex() => Convert.ToHexString(_hash.GetHashAndReset());

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        inner.Write(buffer);
        BytesWritten += buffer.Length;
        onWritten?.Invoke(BytesWritten);
    }

    public override void Flush() => inner.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => BytesWritten;
    public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
