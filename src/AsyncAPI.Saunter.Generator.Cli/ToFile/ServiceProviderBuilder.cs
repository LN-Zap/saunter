using System.Runtime.Loader;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Saunter;

namespace AsyncAPI.Saunter.Generator.Cli.ToFile;

internal interface IServiceProviderBuilder
{
    IServiceProvider BuildServiceProvider(string startupAssembly);
}

internal class ServiceProviderBuilder(ILogger<ServiceProviderBuilder> logger) : IServiceProviderBuilder
{
    public IServiceProvider BuildServiceProvider(string startupAssembly)
    {
        var fullPath = Path.GetFullPath(startupAssembly);
        var basePath = Path.GetDirectoryName(fullPath); // Base path of the target assembly

        if (string.IsNullOrEmpty(basePath) || !Directory.Exists(basePath))
        {
            logger.LogError(
                $"Base directory for assembly '{startupAssembly}' could not be determined or does not exist: {basePath}");
            throw new DirectoryNotFoundException(
                $"Could not find directory for assembly '{startupAssembly}'. Path: {basePath}");
        }

        var originalCurrentDirectory = Directory.GetCurrentDirectory();
        logger.LogInformation($"[Tool] Original current directory: {originalCurrentDirectory}");
        try
        {
            if (originalCurrentDirectory != basePath)
            {
                logger.LogInformation($"[Tool] Setting current directory to target assembly directory: {basePath}");
                Directory.SetCurrentDirectory(basePath);
                logger.LogInformation($"[Tool] Current directory is now: {Directory.GetCurrentDirectory()}"); // Verify
            }
            else
            {
                logger.LogInformation($"[Tool] Current directory is already the target assembly directory.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                $"[Tool] Failed to set current directory to {basePath}. Path resolution errors may occur.");
        }

        // Initialize dependency resolution AFTER potentially changing the directory
        // This Init might add the basePath to probing paths etc.
        DependencyResolver.Init(basePath);

        logger.LogInformation($"Loading startup assembly: {fullPath}");

        var context = new AssemblyLoadContext($"SaunterToolContext_{Path.GetFileNameWithoutExtension(fullPath)}",
            isCollectible: true);
        var assembly = context.LoadFromAssemblyPath(fullPath);

        // --- Configure AppServiceProviderFactory ---
        var reporter = new OperationReporter(new OperationReportHandler(
            m => logger.LogError(m), // Error
            m => logger.LogWarning(m), // Warn
            m => logger.LogInformation(m), // Info
            m => logger.LogDebug(m) // Debug
        ));

        logger.LogDebug("[Tool] Creating AppServiceProviderFactory...");
        var appServiceProviderFactory = new AppServiceProviderFactory(assembly, reporter);

        logger.LogInformation(
            "[Tool] Calling AppServiceProviderFactory.Create([])... (This finds Program/Startup and builds the host)");
        IServiceProvider serviceProvider = null;
        try
        {
            serviceProvider = appServiceProviderFactory.Create([]); // This can throw if host build fails
            logger.LogInformation("[Tool] ServiceProvider created successfully.");

            // Check if the service provider has AsyncAPI services registered
            var asyncApiOptions = serviceProvider?.GetService<IOptions<AsyncApiOptions>>();
            if (asyncApiOptions == null)
            {
                logger.LogWarning("[Tool] ServiceProvider lacks AsyncAPI services. Creating fallback with minimal AsyncAPI services.");
                serviceProvider = CreateFallbackServiceProvider();
                logger.LogInformation("[Tool] Fallback ServiceProvider created successfully.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Tool] Failed to create ServiceProvider via AppServiceProviderFactory. Attempting fallback with minimal AsyncAPI services.");

            // Create a fallback service provider with AsyncAPI services
            serviceProvider = CreateFallbackServiceProvider();
            logger.LogInformation("[Tool] Fallback ServiceProvider created successfully.");
        }

        RestoreOriginalDirectory(originalCurrentDirectory);

        return serviceProvider;
    }

    private IServiceProvider CreateFallbackServiceProvider()
    {
        logger.LogInformation("[Tool] Creating fallback ServiceProvider with minimal AsyncAPI services.");

        var services = new ServiceCollection();

        // Add logging
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

        // Add AsyncAPI services (mimicking EventBusAsyncApiStartup.configureAsyncApiServices)
        services.AddAsyncApiSchemaGeneration(opt =>
        {
            opt.Middleware.UiBaseRoute = "/asyncapi/";
            // Skip custom type name generator to avoid additional dependencies
        });

        logger.LogDebug("[Tool] Fallback service collection configured with AsyncAPI services.");

        return services.BuildServiceProvider();
    }

    private void RestoreOriginalDirectory(string originalCurrentDirectory)
    {
        try
        {
            var currentDir = Directory.GetCurrentDirectory();
            if (currentDir != originalCurrentDirectory && Directory.Exists(originalCurrentDirectory))
            {
                logger.LogInformation($"[Tool] Restoring current directory to: {originalCurrentDirectory}");
                Directory.SetCurrentDirectory(originalCurrentDirectory);
            }
            else if (currentDir == originalCurrentDirectory)
            {
                logger.LogDebug($"[Tool] Current directory already matches original directory.");
            }
            else
            {
                logger.LogWarning(
                    $"[Tool] Cannot restore original directory '{originalCurrentDirectory}' as it no longer exists or is invalid.");
            }
        }
        catch (Exception ex)
        {
            // This shouldn't prevent the tool from finishing, but log it.
            logger.LogWarning(ex, "[Tool] Failed to restore original current directory.");
        }
    }
}
