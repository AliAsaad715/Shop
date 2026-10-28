namespace Shop.Modules.Products.Images;

// Leaves the request's original stream open; limits chunked bodies as well as Content-Length uploads.
internal sealed class UploadReadStream(Stream inner) : Stream
{
    private long _read;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, Allowed(count));
        Advance(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken);
        Advance(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Allowed(int count) => (int)Math.Min(count, Math.Max(1, ImageUploadLimits.RequestBytes - _read + 1));

    private void Advance(int count)
    {
        _read += count;
        if (_read > ImageUploadLimits.RequestBytes)
        {
            throw new ImageUploadTooLargeException();
        }
    }
}
