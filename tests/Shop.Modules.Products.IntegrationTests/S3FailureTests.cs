using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Shop.Modules.Products.Images;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

public sealed class S3FailureTests
{
    private static readonly string Key = $"products/{Guid.NewGuid():N}.png";

    [Fact]
    public async Task DeadlineBoundsAnUploadAndPreservesItsTimeoutAsTheCause()
    {
        using var client = new ControlledClient { Put = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(); } };
        var storage = Storage(client);
        await using var content = new MemoryStream([1, 2, 3]);
        var error = await Assert.ThrowsAsync<ImageStorageUnavailableException>(() => storage.SaveAsync(content, ".png", default));
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedIntoAStorageOutage()
    {
        using var client = new ControlledClient { Get = (_, _, ct) => Task.FromCanceled<GetObjectResponse>(ct) };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Storage(client).OpenReadAsync(Key, cancelled.Token));
    }

    [Fact]
    public async Task DisposingTheReturnedStreamDisposesTheSdkResponseBody()
    {
        var body = new TrackedStream([1, 2, 3]);
        using var client = new ControlledClient { Get = (_, _, _) => Task.FromResult(new GetObjectResponse { ResponseStream = body }) };
        var stored = await Storage(client).OpenReadAsync(Key, default);
        Assert.NotNull(stored);
        Assert.False(body.WasDisposed);
        await stored.Content.DisposeAsync();
        Assert.True(body.WasDisposed);
    }

    [Fact]
    public async Task DeadlineAlsoAppliesWhileReadingTheResponseBody()
    {
        using var client = new ControlledClient
        {
            Get = (_, _, _) => Task.FromResult(new GetObjectResponse { ResponseStream = new WaitingStream() }),
        };
        var stored = await Storage(client).OpenReadAsync(Key, default);
        Assert.NotNull(stored);
        await using var stream = stored.Content;
        await Assert.ThrowsAsync<ImageStorageUnavailableException>(() => stream.ReadAsync(new byte[1]).AsTask());
    }

    [Fact]
    public async Task OverlargeNonSeekableContentIsRejectedBeforeAnyS3Write()
    {
        var calls = 0;
        using var client = new ControlledClient { Put = (_, _) => { calls++; return Task.FromResult(new PutObjectResponse()); } };
        await using var input = new MemoryStream(new byte[ImageUploadLimits.ImageBytes + 1]);
        using var nonSeekable = new UploadReadStream(input);
        await Assert.ThrowsAsync<ImageUploadTooLargeException>(() => Storage(client).SaveAsync(nonSeekable, ".png", default));
        Assert.Equal(0, calls);
    }

    private static S3ImageStorage Storage(IAmazonS3 client) => new(client, Options.Create(new ImageStorageOptions
    {
        Provider = "S3", S3 = new() { Bucket = "shop-images", OperationTimeout = TimeSpan.FromMilliseconds(100) },
    }));

    private sealed class ControlledClient() : AmazonS3Client(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public Func<PutObjectRequest, CancellationToken, Task<PutObjectResponse>> Put { get; init; } = (_, _) => Task.FromResult(new PutObjectResponse());
        public Func<string, string, CancellationToken, Task<GetObjectResponse>> Get { get; init; } = (_, _, _) => Task.FromResult(new GetObjectResponse());
        public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default) => Put(request, cancellationToken);
        public override Task<GetObjectResponse> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken = default) => Get(bucketName, key, cancellationToken);
    }

    private sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class WaitingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
