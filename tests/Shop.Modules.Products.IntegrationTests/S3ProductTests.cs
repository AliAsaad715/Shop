using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shop.Modules.Products.Creation;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Images;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

[CollectionDefinition("S3 images")]
public sealed class S3ImagesCollection : ICollectionFixture<S3ProductFixture>;

[Collection("S3 images")]
public sealed class S3ProductTests(S3ProductFixture fixture)
{
    [Theory]
    [InlineData(".png", "image/png")]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".webp", "image/webp")]
    public async Task UploadOnOneHostCanBeReadOnAnIndependentHost(string extension, string type)
    {
        var bytes = S3TestImages.Bytes(extension);
        using var response = await PostAsync(fixture.Client, bytes);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var product = (await response.Content.ReadFromJsonAsync<CreatedProductResponse>())!;
        Assert.EndsWith(extension, product.ImageUrl);
        Assert.Equal($"/api/v1/products/{product.Id}", response.Headers.Location?.ToString());
        var other = await fixture.CreateAppAsync();
        using var client = other.GetTestClient();
        using var image = await client.GetAsync(product.ImageUrl);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(type, image.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(image.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(bytes, await image.Content.ReadAsByteArrayAsync());
        var s3 = fixture.App.Services.GetRequiredService<IAmazonS3>();
        using var stored = await s3.GetObjectAsync(S3ProductFixture.Bucket, product.ImageUrl["/images/".Length..]);
        Assert.Equal(type, stored.Headers.ContentType);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("Customer", HttpStatusCode.Forbidden)]
    public async Task RejectedAuthorizationNeverWritesAnObject(string? role, HttpStatusCode status)
    {
        var count = await fixture.ObjectCountAsync();
        using var response = await PostAsync(fixture.Client, S3TestImages.Bytes(), role);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(count, await fixture.ObjectCountAsync());
    }

    [Theory]
    [InlineData(2 * 1024 * 1024, HttpStatusCode.Created)]
    [InlineData(2 * 1024 * 1024 + 1, HttpStatusCode.RequestEntityTooLarge)]
    public async Task ImageLimitIsTheSameWithS3(int length, HttpStatusCode status)
    {
        var bytes = new byte[length];
        S3TestImages.Bytes().CopyTo(bytes, 0);
        var count = await fixture.ObjectCountAsync();
        using var response = await PostAsync(fixture.Client, bytes);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(count + (status == HttpStatusCode.Created ? 1 : 0), await fixture.ObjectCountAsync());
    }

    [Fact]
    public async Task UnsupportedImageDoesNotReachS3()
    {
        var count = await fixture.ObjectCountAsync();
        using var response = await PostAsync(fixture.Client, "fake png"u8.ToArray());
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(count, await fixture.ObjectCountAsync());
    }

    [Fact]
    public async Task DatabaseFailureDeletesTheObject()
    {
        var count = await fixture.ObjectCountAsync();
        var products = await ProductCountAsync();
        fixture.SaveFailure.FailNext = true;
        using var response = await PostAsync(fixture.Client, S3TestImages.Bytes());
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(count, await fixture.ObjectCountAsync());
        Assert.Equal(products, await ProductCountAsync());
    }

    [Fact]
    public async Task DeleteFailureDoesNotReplaceTheOriginalDatabaseError()
    {
        var s3 = fixture.App.Services.GetRequiredService<IAmazonS3>();
        var before = (await s3.ListObjectsV2Async(new() { BucketName = S3ProductFixture.Bucket })).S3Objects?.Select(item => item.Key).ToHashSet() ?? [];
        var app = await fixture.CreateAppAsync(failDelete: true);
        using var client = app.GetTestClient();
        fixture.Errors.Clear();
        fixture.SaveFailure.FailNext = true;
        using var response = await PostAsync(client, S3TestImages.Bytes());
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(fixture.Errors, error => error is InvalidOperationException { Message: "Simulated database save failure." });
        Assert.Contains(fixture.Errors, error => error is ImageStorageUnavailableException);
        var after = await s3.ListObjectsV2Async(new() { BucketName = S3ProductFixture.Bucket });
        var orphan = Assert.Single(after.S3Objects!, item => !before.Contains(item.Key));
        await s3.DeleteObjectAsync(S3ProductFixture.Bucket, orphan.Key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingBucketOrInvalidCredentialsReturn503RatherThan404OrLocalFallback(bool missingBucket)
    {
        var app = await fixture.CreateAppAsync(bucket: missingBucket ? "does-not-exist" : null, secret: missingBucket ? null : "wrong-secret");
        using var client = app.GetTestClient();
        var count = await ProductCountAsync();
        using var uploaded = await PostAsync(client, S3TestImages.Bytes());
        await AssertUnavailableAsync(uploaded);
        using var image = await client.GetAsync($"/images/products/{Guid.NewGuid():N}.png");
        await AssertUnavailableAsync(image);
        Assert.Equal(count, await ProductCountAsync());
        var health = await app.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(entry => entry.Name == "product-images");
        Assert.Equal(HealthStatus.Degraded, health.Status);
    }

    [Fact]
    public async Task MissingObjectAndInvalidKeysRemain404AndDeleteIsIdempotent()
    {
        using var missing = await fixture.Client.GetAsync($"/images/products/{Guid.NewGuid():N}.png");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IImageStorage>();
        Assert.Null(await storage.OpenReadAsync("products/../outside.png", default));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync("products/../outside.png", default));
        await using var input = new MemoryStream(S3TestImages.Bytes());
        var key = await storage.SaveAsync(input, ".png", default);
        await storage.DeleteAsync(key, default);
        await storage.DeleteAsync(key, default);
        Assert.Null(await storage.OpenReadAsync(key, default));
    }

    [Fact]
    public async Task NonSeekableInputStoresTheSameBytes()
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IImageStorage>();
        var bytes = S3TestImages.Bytes();
        await using var input = new MemoryStream(bytes);
        using var nonSeekable = new UploadReadStream(input);
        var key = await storage.SaveAsync(nonSeekable, ".png", default);
        var image = await storage.OpenReadAsync(key, default);
        Assert.NotNull(image);
        await using var stream = image.Content;
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        Assert.Equal(bytes, output.ToArray());
    }

    private async Task<HttpResponseMessage> PostAsync(HttpClient client, byte[] bytes, string? role = "Admin")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/products");
        if (role is not null) request.Headers.Add("X-Test-Role", role);
        var form = new MultipartFormDataContent
        {
            { new StringContent("S3 product"), "name" }, { new StringContent("Storage integration"), "description" },
            { new StringContent("10.00"), "price" }, { new StringContent(fixture.CategoryId.ToString()), "categoryId" },
        };
        var image = new ByteArrayContent(bytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        form.Add(image, "image", "../../untrusted.exe");
        request.Content = form;
        return await client.SendAsync(request);
    }

    private async Task<int> ProductCountAsync()
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ProductsDbContext>().Products.CountAsync();
    }

    private static async Task AssertUnavailableAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("common.service_unavailable", json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("test-secret", json.RootElement.ToString());
        Assert.DoesNotContain("wrong-secret", json.RootElement.ToString());
    }
}

internal static class S3TestImages
{
    public static byte[] Bytes(string extension = ".png") => extension switch
    {
        ".jpg" => [0xff, 0xd8, 0xff, 0xd9],
        ".webp" => "RIFF0000WEBPdata"u8.ToArray(),
        _ => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGPQSFkFAAHuATczvmbSAAAAAElFTkSuQmCC"),
    };
}
