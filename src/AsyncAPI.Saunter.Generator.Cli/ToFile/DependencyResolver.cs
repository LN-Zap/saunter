using System.Reflection;
using System.Runtime.Loader;

namespace AsyncAPI.Saunter.Generator.Cli.ToFile;

/// <summary>
/// Loads the startup assembly into the default <see cref="AssemblyLoadContext"/> and resolves any dependency the
/// host could not bind on its own through <see cref="AssemblyDependencyResolver"/> (i.e. via <c>.deps.json</c>).
/// </summary>
/// <remarks>
/// Everything is loaded into a single load context, so there is only ever one copy of each assembly (and therefore one
/// identity for each type, e.g. <c>System.Diagnostics.Metrics.IMeterFactory</c>). Loading the same assembly a second
/// time from another path or load context (as <see cref="Assembly.LoadFile(string)"/> does) produces duplicate types
/// that dependency injection treats as unrelated services.
/// </remarks>
internal static class DependencyResolver
{
    private static readonly object Sync = new();
    private static readonly List<AssemblyDependencyResolver> Resolvers = [];
    private static readonly HashSet<string> RegisteredComponents = new(StringComparer.OrdinalIgnoreCase);
    private static bool _hooked;

    /// <summary>
    /// Makes the tool's own dependencies resolvable. Must run before any code that uses them, because when the tool
    /// runs with an application's .deps.json (see <see cref="AppContextLauncher"/>) only the application's assemblies
    /// are known to the host.
    /// </summary>
    public static void Initialize()
    {
        Register(typeof(DependencyResolver).Assembly.Location, first: false);
    }

    public static Assembly LoadStartupAssembly(string startupAssemblyPath)
    {
        var fullPath = Path.GetFullPath(startupAssemblyPath);

        // Assemblies the host cannot bind are resolved via the application's .deps.json first, then the tool's.
        Register(fullPath, first: true);
        Initialize();

        var assemblyName = AssemblyName.GetAssemblyName(fullPath);
        return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
    }

    private static void Register(string componentAssemblyPath, bool first)
    {
        if (string.IsNullOrEmpty(componentAssemblyPath) || !File.Exists(componentAssemblyPath))
        {
            return;
        }

        lock (Sync)
        {
            if (RegisteredComponents.Add(componentAssemblyPath))
            {
                try
                {
                    Resolvers.Insert(first ? 0 : Resolvers.Count, new AssemblyDependencyResolver(componentAssemblyPath));
                }
                catch (InvalidOperationException)
                {
                    // No usable dependency information for this component; rely on the default probing only.
                }
            }

            if (!_hooked)
            {
                AssemblyLoadContext.Default.Resolving += OnResolving;
                _hooked = true;
            }
        }
    }

    private static Assembly OnResolving(AssemblyLoadContext context, AssemblyName requested)
    {
        AssemblyDependencyResolver[] resolvers;
        lock (Sync)
        {
            resolvers = [.. Resolvers];
        }

        foreach (var resolver in resolvers)
        {
            var path = resolver.ResolveAssemblyToPath(requested);
            if (path is null)
            {
                continue;
            }

            // An assembly with this name may already be loaded (e.g. an older framework copy). Loading a second copy
            // would only produce duplicate types, so fail the load instead.
            if (context.Assemblies.Any(a => string.Equals(a.GetName().Name, requested.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return context.LoadFromAssemblyPath(path);
        }

        return null;
    }
}
