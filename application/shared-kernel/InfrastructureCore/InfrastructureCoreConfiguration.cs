using System.Net.Sockets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlatformPlatform.SharedKernel.ApplicationCore.Services;
using PlatformPlatform.SharedKernel.DomainCore.DomainEvents;
using PlatformPlatform.SharedKernel.DomainCore.Persistence;
using PlatformPlatform.SharedKernel.InfrastructureCore.Persistence;
using PlatformPlatform.SharedKernel.InfrastructureCore.Services;

namespace PlatformPlatform.SharedKernel.InfrastructureCore;

public static class InfrastructureCoreConfiguration
{
    private static readonly bool IsRunningInAzure = Environment.GetEnvironmentVariable("KEYVAULT_URL") is not null;

    [UsedImplicitly]
    public static IServiceCollection ConfigureDatabaseContext<T>(
        this IServiceCollection services,
        IHostApplicationBuilder builder,
        string connectionName
    ) where T : DbContext
    {
        var connectionString = IsRunningInAzure
            ? Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING")
            : builder.Configuration.GetConnectionString(connectionName);

        builder.Services.AddSqlServer<T>(connectionString, optionsBuilder => { optionsBuilder.UseAzureSqlDefaults(); });
        builder.EnrichSqlServerDbContext<T>();

        return services;
    }

    public static IServiceCollection AddBlobStorage(
        this IServiceCollection services,
        IHostApplicationBuilder builder,
        params (string ConnectionName, string EnvironmentVariable)[] connections
    )
    {
        if (IsRunningInAzure)
        {
            foreach (var connection in connections)
            {
                // In Azure a container can have multiple storage accounts, so we need to specify the account
                var storageEndpointUri = new Uri(Environment.GetEnvironmentVariable(connection.EnvironmentVariable)!);
                services.AddKeyedSingleton<IBlobStorage, BlobStorage>(connection.ConnectionName,
                    (_, _) => new BlobStorage(new BlobServiceClient(storageEndpointUri, GetDefaultAzureCredential())));
            }
        }
        else
        {
            foreach (var connection in connections)
            {
                builder.AddAzureBlobService(connection.ConnectionName);
            }
        }

        services.AddTransient<IBlobStorage, BlobStorage>();
        return services;
    }

    [UsedImplicitly]
    public static IServiceCollection ConfigureInfrastructureCoreServices<T>(
        this IServiceCollection services,
        Assembly assembly
    ) where T : DbContext
    {
        services.AddScoped<IUnitOfWork, UnitOfWork>(provider => new UnitOfWork(provider.GetRequiredService<T>()));
        services.AddScoped<IDomainEventCollector, DomainEventCollector>(provider =>
            new DomainEventCollector(provider.GetRequiredService<T>()));

        services.RegisterRepositories(assembly);

        if (IsRunningInAzure)
        {
            var keyVaultUri = new Uri(Environment.GetEnvironmentVariable("KEYVAULT_URL")!);
            services.AddSingleton(_ => new SecretClient(keyVaultUri, GetDefaultAzureCredential()));

            services.AddTransient<IEmailService, AzureEmailService>();
        }
        else
        {
            services.AddTransient<IEmailService, DevelopmentEmailService>();
        }

        return services;
    }

    private static DefaultAzureCredential GetDefaultAzureCredential()
    {
        var managedIdentityClientId = Environment.GetEnvironmentVariable("MANAGED_IDENTITY_CLIENT_ID")!;
        var credentialOptions = new DefaultAzureCredentialOptions { ManagedIdentityClientId = managedIdentityClientId };
        return new DefaultAzureCredential(credentialOptions);
    }

    [UsedImplicitly]
    private static IServiceCollection RegisterRepositories(this IServiceCollection services, Assembly assembly)
    {
        // Scrutor will scan the assembly for all classes that implement the IRepository
        // and register them as a service in the container.
        services.Scan(scan => scan
            .FromAssemblies(assembly)
            .AddClasses(classes => classes.Where(type =>
                type.IsClass && (type.IsNotPublic || type.IsPublic)
                             && type.BaseType is { IsGenericType: true } &&
                             type.BaseType.GetGenericTypeDefinition() == typeof(RepositoryBase<,>)))
            .AsImplementedInterfaces()
            .WithScopedLifetime());

        return services;
    }

    public static void ApplyMigrations<T>(this IServiceProvider services) where T : DbContext
    {
        using var scope = services.CreateScope();

        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(nameof(InfrastructureCoreConfiguration));

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
        logger.LogInformation("Applying database migrations. Version: {Version}.", version);

        var retryCount = 1;
        while (retryCount <= 20)
        {
            try
            {
                if (retryCount % 5 == 0) logger.LogInformation("Waiting for databases to be ready...");

                var dbContext = scope.ServiceProvider.GetService<T>() ??
                                throw new UnreachableException("Missing DbContext.");

                if (dbContext.Database.GetConnectionString() is null)
                {
                    logger.LogCritical("Missing connection string in DbContext. Aborting migration.");
                    return; // When OpenApiGenerateDocumentsOnBuild the connection string is not available
                }

                var strategy = dbContext.Database.CreateExecutionStrategy();

                strategy.Execute(() => dbContext.Database.Migrate());

                logger.LogInformation("Finished migrating database.");

                break;
            }
            catch (SqlException ex) when (ex.Message.Contains("an error occurred during the pre-login handshake"))
            {
                // Known error in Aspire, when SQL Server is not ready
                retryCount++;
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
            catch (SocketException ex) when (ex.Message.Contains("Invalid argument"))
            {
                // Known error in Aspire, when SQL Server is not ready
                retryCount++;
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred while applying migrations.");

                // Wait for the logger to flush
                Thread.Sleep(TimeSpan.FromSeconds(1));

                break;
            }
        }
    }
}