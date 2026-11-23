using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Shop.Modules.Products.Images;

internal static class ImageStorageRegistration
{
    public static IServiceCollection AddProductImageStorage(this IServiceCollection services)
    {
        services.AddOptions<ImageStorageOptions>().BindConfiguration(ImageStorageOptions.Section)
            .Validate(ImageStorageOptions.IsValid,
                "Images:Provider must be Local (with RootPath) or S3 (with an HTTP(S) Endpoint, Bucket, AccessKey, SecretKey, Region and a positive timeout up to 30 seconds).")
            .ValidateOnStart();
        services.AddSingleton<IAmazonS3>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ImageStorageOptions>>().Value;
            if (!options.IsS3) throw new InvalidOperationException("S3 storage is not configured.");
            return new AmazonS3Client(new BasicAWSCredentials(options.S3.AccessKey, options.S3.SecretKey), new AmazonS3Config
            {
                ServiceURL = options.S3.Endpoint, AuthenticationRegion = options.S3.Region, ForcePathStyle = true,
                MaxErrorRetry = 1,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
        });
        services.AddScoped<LocalImageStorage>();
        services.AddScoped<S3ImageStorage>();
        services.AddScoped<IImageStorage>(provider => provider.GetRequiredService<IOptions<ImageStorageOptions>>().Value.IsS3
            ? provider.GetRequiredService<S3ImageStorage>() : provider.GetRequiredService<LocalImageStorage>());
        services.AddScoped<ImageImporter>();
        services.AddHealthChecks().AddCheck<ImageStorageHealthCheck>("product-images", failureStatus: HealthStatus.Degraded);
        return services;
    }
}

internal sealed class ImageStorageHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (!scope.ServiceProvider.GetRequiredService<IOptions<ImageStorageOptions>>().Value.IsS3)
            return HealthCheckResult.Healthy("Local image storage selected.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // /health also probes the warehouse; keep the aggregate response inside the container's 3-second probe.
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await scope.ServiceProvider.GetRequiredService<S3ImageStorage>().EnsureAvailableAsync(deadline.Token);
            return HealthCheckResult.Healthy("Product image bucket is available.");
        }
        catch (Exception error) when (error is ImageStorageUnavailableException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Product image storage is unavailable.", error);
        }
    }
}
