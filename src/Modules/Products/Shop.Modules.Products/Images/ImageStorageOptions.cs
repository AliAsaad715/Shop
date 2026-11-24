namespace Shop.Modules.Products.Images;

internal sealed class ImageStorageOptions
{
    public const string Section = "Images";
    public string Provider { get; set; } = "Local";
    public string RootPath { get; set; } = "App_Data/images";
    public S3ImageStorageOptions S3 { get; set; } = new();
    public bool IsS3 => string.Equals(Provider, "S3", StringComparison.OrdinalIgnoreCase);
    public bool IsLocal => string.Equals(Provider, "Local", StringComparison.OrdinalIgnoreCase);

    public static bool IsValid(ImageStorageOptions options) =>
        (options.IsLocal && !string.IsNullOrWhiteSpace(options.RootPath))
        || (options.IsS3 && options.S3.IsValid());
}

internal sealed class S3ImageStorageOptions
{
    public string Endpoint { get; set; } = "";
    public string Bucket { get; set; } = "shop-images";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsValid() =>
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && Bucket.Length is >= 3 and <= 63 && Bucket.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && char.IsAsciiLetterOrDigit(Bucket[0]) && char.IsAsciiLetterOrDigit(Bucket[^1])
        && !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey)
        && !string.IsNullOrWhiteSpace(Region)
        && OperationTimeout > TimeSpan.Zero && OperationTimeout <= TimeSpan.FromSeconds(30);
}
