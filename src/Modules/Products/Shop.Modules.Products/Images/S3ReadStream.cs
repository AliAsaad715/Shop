using Amazon.S3.Model;

namespace Shop.Modules.Products.Images;

internal sealed class S3ReadStream(GetObjectResponse response, CancellationTokenSource deadline, CancellationToken requestToken) : Stream
{
    private readonly Stream _inner = response.ResponseStream;
    private bool _disposed;
    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try { return await _inner.ReadAsync(buffer, linked.Token); }
        catch (Exception error) when (S3ImageStorage.IsStorageFailure(error)
            && !(error is OperationCanceledException && (requestToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)))
        {
            throw new ImageStorageUnavailableException(error);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            response.Dispose();
            deadline.Dispose();
        }
        base.Dispose(disposing);
    }
}
