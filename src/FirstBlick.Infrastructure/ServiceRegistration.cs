using Amazon.Runtime;
using Amazon.S3;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FirstBlick.Infrastructure;

public static class ServiceRegistration
{
    public static IServiceCollection AddFirstBlickStorage(this IServiceCollection services, IConfiguration config)
    {
        var connection = config.GetConnectionString("Database") ?? throw new InvalidOperationException("ConnectionStrings:Database is required.");
        services.AddScoped<TenantScope>();
        services.AddDbContext<FirstBlickDbContext>(o => o.UseNpgsql(connection).EnableDetailedErrors(false));
        services.AddScoped<EntityStore>();
        services.AddScoped<OutboxProcessor>();
        var endpoint = config["Storage:Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
                new BasicAWSCredentials(config["Storage:AccessKey"] ?? throw new InvalidOperationException("Storage access key is required."),
                    config["Storage:SecretKey"] ?? throw new InvalidOperationException("Storage secret key is required.")),
                new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = config["Storage:Region"] ?? "us-east-1" }));
            services.AddSingleton<IObjectStore, S3ObjectStore>();
        }
        else services.AddSingleton<IObjectStore, UnconfiguredObjectStore>();
        return services;
    }
}
