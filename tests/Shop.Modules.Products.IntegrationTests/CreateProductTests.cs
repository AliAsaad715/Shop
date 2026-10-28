using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Products.Creation;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Images;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

[Collection("Products database")]
public sealed class CreateProductTests(CreateProductFixture fixture) : IClassFixture<CreateProductFixture>
{
    // Real one-pixel image files, independent of the client's filename and Content-Type.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGPQSFkFAAHuATczvmbSAAAAAElFTkSuQmCC";
    private const string Jpeg = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDmaKKK+oPnz//Z";
    private const string Webp = "UklGRjgAAABXRUJQVlA4ICwAAADQAQCdASoBAAEAAUAmJaACdLoB+AADsAD+8FMf/ziWOp3dB//yC5YXXEYAAA==";

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized, ErrorCodes.Common.Unauthorized)]
    [InlineData(Roles.Customer, HttpStatusCode.Forbidden, ErrorCodes.Common.Forbidden)]
    public async Task OnlyAdminsCanCreateProducts(string? role, HttpStatusCode status, string code)
    {
        var before = await CountAsync();
        using var response = await PostAsync(Form(), role);

        await AssertProblemAsync(response, status, code);
        Assert.Equal(before, await CountAsync());
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized, ErrorCodes.Common.Unauthorized)]
    [InlineData(Roles.Customer, HttpStatusCode.Forbidden, ErrorCodes.Common.Forbidden)]
    public async Task AuthorizationRejectsOversizedBodiesBeforeUploadProcessing(string? role, HttpStatusCode status, string code)
    {
        using var response = await PostAsync(Form(PaddedImage(3 * 1024 * 1024)), role);

        await AssertProblemAsync(response, status, code);
    }

    [Fact]
    public async Task NonMultipartContentReturns415()
    {
        using var response = await PostAsync(new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType, ErrorCodes.Common.UnsupportedMediaType);
    }

    [Fact]
    public async Task MultipleImagesReturn422WithoutWritingFiles()
    {
        var files = Files();
        var form = Form();
        form.Add(new ByteArrayContent(Convert.FromBase64String(Png)), "image", "second.png");
        using var response = await PostAsync(form);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.Common.ValidationFailed);
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task StorageLimitFailureRemovesThePartialFile()
    {
        var files = Files();
        await using var scope = fixture.App.Services.CreateAsyncScope();
        await using var content = new MemoryStream(PaddedImage(3 * 1024 * 1024));
        var storage = scope.ServiceProvider.GetRequiredService<IImageStorage>();

        await Assert.ThrowsAsync<ImageUploadTooLargeException>(() => storage.SaveAsync(content, ".png", CancellationToken.None));
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData(Png, ".png", "image/png")]
    [InlineData(Jpeg, ".jpg", "image/jpeg")]
    [InlineData(Webp, ".webp", "image/webp")]
    public async Task AdminUploadUsesSignatureAndGeneratedFilenameAndServesTheSameBytes(string image, string extension, string contentType)
    {
        var bytes = Convert.FromBase64String(image);
        using var response = await PostAsync(Form(bytes, "../../untrusted.exe", "text/html"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreatedProductResponse>())!;

        Assert.Equal($"/api/v1/products/{created.Id}", response.Headers.Location?.ToString());
        Assert.Equal(7, created.Id.Version);
        Assert.Equal("Test product", created.Name);
        Assert.Equal(19.95m, created.Price);
        Assert.Equal(TimeSpan.Zero, created.CreatedAt.Offset);
        var fileName = Path.GetFileName(created.ImageUrl);
        Assert.EndsWith(extension, fileName);
        Assert.True(Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "N", out var fileId));
        Assert.Equal(7, fileId.Version);
        Assert.DoesNotContain("untrusted", fileName);

        using var imageResponse = await fixture.Client.GetAsync(created.ImageUrl);
        Assert.Equal(HttpStatusCode.OK, imageResponse.StatusCode);
        Assert.Equal(contentType, imageResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(imageResponse.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(bytes, await imageResponse.Content.ReadAsByteArrayAsync());

        await using var scope = fixture.App.Services.CreateAsyncScope();
        var product = await scope.ServiceProvider.GetRequiredService<ProductsDbContext>().Products.SingleAsync(product => product.Id == created.Id);
        Assert.Equal("products/" + fileName, product.ImagePath);
        Assert.Equal(fixture.CategoryId, product.CategoryId);
    }

    [Fact]
    public async Task ExactlyTwoMegabytesIsAccepted()
    {
        using var response = await PostAsync(Form(PaddedImage(ImageUploadLimits.ImageBytes)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData(false, 2 * 1024 * 1024 + 1)]
    [InlineData(false, 3 * 1024 * 1024)]
    [InlineData(true, 3 * 1024 * 1024)]
    public async Task OversizedFilesAndChunkedBodiesReturn413WithoutSideEffects(bool chunked, int length)
    {
        var before = await CountAsync();
        var files = Files();
        HttpContent content = Form(PaddedImage(length));
        if (chunked)
        {
            content = new UnknownLengthContent(content);
        }

        using var response = await PostAsync(content);
        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, ErrorCodes.Products.ImageTooLarge);
        Assert.Equal(before, await CountAsync());
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData("<svg>not an image</svg>")]
    [InlineData("GIF89a")]
    [InlineData("RIFFxxxxNOTP")]
    public async Task SpoofedOrUnsupportedSignaturesReturn415(string body)
    {
        var files = Files();
        using var response = await PostAsync(Form(System.Text.Encoding.UTF8.GetBytes(body), "photo.png", "image/png"));

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType, ErrorCodes.Products.UnsupportedImageType);
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("description", "")]
    [InlineData("price", "-1")]
    [InlineData("price", "1.234")]
    [InlineData("price", "10000000000000000")]
    [InlineData("categoryId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("categoryId", "019a2180-2222-7000-8000-000000000001")]
    public async Task InvalidFieldsReturn422WithoutWritingImages(string field, string value)
    {
        var files = Files();
        using var response = await PostAsync(Form(field: field, value: value));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.Common.ValidationFailed);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData("price", "abc")]
    [InlineData("categoryId", "abc")]
    public async Task UnparseableFormFieldsReturn400(string field, string value)
    {
        using var response = await PostAsync(Form(field: field, value: value));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Common.BadRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrEmptyImageReturns422(bool includeEmptyFile)
    {
        using var response = await PostAsync(Form(includeEmptyFile ? Array.Empty<byte>() : null, includeImage: includeEmptyFile));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.Common.ValidationFailed);
    }

    [Fact]
    public async Task DatabaseSaveFailureRemovesTheUploadedImage()
    {
        var before = await CountAsync();
        var files = Files();
        fixture.SaveFailure.FailNext = true;
        using var response = await PostAsync(Form());

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, ErrorCodes.Common.InternalError);
        Assert.Equal(before, await CountAsync());
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData("../../outside.png")]
    [InlineData("products/../../outside.png")]
    [InlineData("products/not-a-generated-name.png")]
    public async Task ImageStorageRejectsPathsOutsideGeneratedKeys(string path)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IImageStorage>();

        Assert.Null(await storage.OpenReadAsync(path, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync(path, CancellationToken.None));
    }

    private MultipartFormDataContent Form(byte[]? bytes = null, string filename = "photo.png", string contentType = "image/png",
        string? field = null, string? value = null, bool includeImage = true)
    {
        var form = new MultipartFormDataContent();
        var fields = new Dictionary<string, string>
        {
            ["name"] = "Test product",
            ["description"] = "A product created by an HTTP integration test.",
            ["price"] = "19.95",
            ["categoryId"] = fixture.CategoryId.ToString(),
        };
        if (field is not null)
        {
            fields[field] = value!;
        }

        foreach (var item in fields)
        {
            form.Add(new StringContent(item.Value), item.Key);
        }

        if (includeImage)
        {
            var image = new ByteArrayContent(bytes ?? Convert.FromBase64String(Png));
            image.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(image, "image", filename);
        }

        return form;
    }

    private async Task<HttpResponseMessage> PostAsync(HttpContent content, string? role = Roles.Admin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/products") { Content = content };
        if (role is not null)
        {
            request.Headers.Add("X-Test-Role", role);
        }

        return await fixture.Client.SendAsync(request);
    }

    private async Task<int> CountAsync()
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ProductsDbContext>().Products.CountAsync();
    }

    private string[] Files() => Directory.GetFiles(fixture.ImageRoot, "*", SearchOption.AllDirectories).Order().ToArray();

    private static byte[] PaddedImage(int length)
    {
        var bytes = new byte[length];
        Convert.FromBase64String(Png).CopyTo(bytes, 0);
        return bytes;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly HttpContent _inner;

        public UnknownLengthContent(HttpContent inner)
        {
            _inner = inner;
            Headers.ContentType = inner.Headers.ContentType;
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
