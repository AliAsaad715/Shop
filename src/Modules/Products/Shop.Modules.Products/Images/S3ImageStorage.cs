using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Shop.Modules.Products.Images;

internal sealed class S3ImageStorage(IAmazonS3 client, IOptions<ImageStorageOptions> options) : IImageStorage
{
    private readonly S3ImageStorageOptions _options = options.Value.S3;

    public async Task<string> SaveAsync(Stream content, string extension, CancellationToken ct)
    {
        if (ImageSignatures.ContentType(extension) is null)
            throw new ArgumentException("Unsupported image extension.", nameof(extension));
        await using var buffered = await ImageContent.ReadAsync(content, ct);
        var key = ImageKey.Create(extension);
        if (!await PutIfAbsentAsync(key, buffered, ct))
            throw new IOException("A generated image key already exists.");
        return key;
    }

    // Lets the explicit importer preserve legacy keys; runtime SaveAsync always generates its own new key.
    internal Task<bool> PutIfAbsentAsync(string key, Stream content, CancellationToken ct)
    {
        if (!ImageKey.IsValid(key)) throw new ArgumentException("Invalid stored image path.", nameof(key));
        return ExecuteAsync(async token =>
        {
            try
            {
                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = _options.Bucket, Key = key, InputStream = content,
                    ContentType = ImageSignatures.ContentType(Path.GetExtension(key)),
                    AutoCloseStream = false, UseChunkEncoding = false, IfNoneMatch = "*",
                }, token);
                return true;
            }
            catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                return false;
            }
        }, ct);
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        if (!ImageKey.IsValid(path)) throw new ArgumentException("Invalid stored image path.", nameof(path));
        return ExecuteAsync(token => client.DeleteObjectAsync(_options.Bucket, path, token), ct);
    }

    public async Task<StoredImage?> OpenReadAsync(string path, CancellationToken ct)
    {
        if (!ImageKey.IsValid(path)) return null;
        var deadline = Deadline(ct);
        try
        {
            var response = await client.GetObjectAsync(_options.Bucket, path, deadline.Token);
            // The result owns both the HTTP response and its deadline until ASP.NET disposes the image stream.
            return new StoredImage(new S3ReadStream(response, deadline, ct), ImageSignatures.ContentType(Path.GetExtension(path))!);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound && error.ErrorCode == "NoSuchKey")
        {
            deadline.Dispose();
            return null;
        }
        catch (Exception error)
        {
            deadline.Dispose();
            if (error is OperationCanceledException && ct.IsCancellationRequested) throw;
            if (IsStorageFailure(error)) throw new ImageStorageUnavailableException(error);
            throw;
        }
    }

    internal Task EnsureAvailableAsync(CancellationToken ct) =>
        ExecuteAsync(token => client.HeadBucketAsync(new HeadBucketRequest { BucketName = _options.Bucket }, token), ct);

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var deadline = Deadline(ct);
        try { return await operation(deadline.Token); }
        catch (Exception error) when (IsStorageFailure(error) && !(error is OperationCanceledException && ct.IsCancellationRequested))
        {
            throw new ImageStorageUnavailableException(error);
        }
    }

    private CancellationTokenSource Deadline(CancellationToken ct)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.OperationTimeout);
        return deadline;
    }

    internal static bool IsStorageFailure(Exception error) =>
        error is AmazonS3Exception or AmazonClientException or HttpRequestException or IOException or OperationCanceledException;
}
