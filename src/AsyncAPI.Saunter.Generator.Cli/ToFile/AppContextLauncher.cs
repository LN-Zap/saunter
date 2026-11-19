using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AsyncAPI.Saunter.Generator.Cli.ToFile;

internal interface IAppContextLauncher
{
    /// <summary>
    /// Re-runs the current command in a child process that is configured by the startup assembly's own
    /// <c>.deps.json</c> and <c>.runtimeconfig.json</c>.
    /// </summary>
    /// <returns><c>true</c> when the command was run in the child process (see <paramref name="exitCode"/>);
    /// <c>false</c> when the caller should run the command in the current process.</returns>
    bool TryRun(string startupAssembly, IReadOnlyList<string> commandArgs, out int exitCode);
}

/// <summary>
/// Runs the tool with the target application's dependency graph, the same way <c>dotnet ef</c> and similar tools do.
/// </summary>
/// <remarks>
/// When the tool loads an application into its own process, assemblies that are part of the shared framework
/// (e.g. System.Diagnostics.DiagnosticSource) are already bound to the framework version the tool runs on. If the
/// application ships a newer copy of such an assembly, the two cannot be unified in-process. Starting the tool with
/// <c>dotnet exec --depsfile app.deps.json --runtimeconfig ...</c> lets the host resolve every assembly exactly as it
/// does when the application itself runs, so the application and the framework share a single copy.
/// </remarks>
internal class AppContextLauncher(ILogger<AppContextLauncher> logger) : IAppContextLauncher
{
    internal const string InAppContextVariable = "ASYNCAPI_SAUNTER_GENERATOR_IN_APP_CONTEXT";

    public bool TryRun(string startupAssembly, IReadOnlyList<string> commandArgs, out int exitCode)
    {
        exitCode = 0;
        if (Environment.GetEnvironmentVariable(InAppContextVariable) == "1")
        {
            return false;
        }

        var fullPath = Path.GetFullPath(startupAssembly);
        var depsFile = Path.ChangeExtension(fullPath, ".deps.json");
        var appRuntimeConfig = Path.ChangeExtension(fullPath, ".runtimeconfig.json");
        if (!File.Exists(depsFile) || !File.Exists(appRuntimeConfig))
        {
            logger.LogWarning($"No .deps.json/.runtimeconfig.json found next to '{fullPath}'. Loading it into the tool's process instead; dependencies newer than the tool's runtime may fail to load.");
            return false;
        }

        var runtimeConfig = CreateRuntimeConfig(appRuntimeConfig);
        if (runtimeConfig is null)
        {
            logger.LogWarning($"'{appRuntimeConfig}' does not reference a shared framework (self-contained?). Loading the application into the tool's process instead.");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(GetDotnetHostPath())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "exec", "--depsfile", depsFile, "--runtimeconfig", runtimeConfig, typeof(AppContextLauncher).Assembly.Location })
            {
                startInfo.ArgumentList.Add(arg);
            }

            foreach (var arg in commandArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }

            startInfo.Environment[InAppContextVariable] = "1";

            logger.LogInformation($"[Tool] Running in the application's context: {startInfo.FileName} {string.Join(' ', startInfo.ArgumentList)}");

            using var process = new Process { StartInfo = startInfo };
            var stdOut = Console.Out;
            var stdErr = Console.Error;
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdOut.WriteLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stdErr.WriteLine(e.Data); } };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            exitCode = process.ExitCode;
            return true;
        }
        finally
        {
            TryDelete(runtimeConfig);
        }
    }

    /// <summary>
    /// Writes a copy of the application's runtimeconfig in which every shared framework is at least the version the
    /// tool itself was built against, so the tool can run even when the application targets an older framework.
    /// </summary>
    private static string CreateRuntimeConfig(string appRuntimeConfig)
    {
        var root = JsonNode.Parse(File.ReadAllText(appRuntimeConfig)) as JsonObject;
        if (root?["runtimeOptions"] is not JsonObject options)
        {
            return null;
        }

        var frameworks = new List<JsonObject>();
        if (options["framework"] is JsonObject single)
        {
            frameworks.Add(single);
        }

        if (options["frameworks"] is JsonArray many)
        {
            frameworks.AddRange(many.OfType<JsonObject>());
        }

        if (frameworks.Count == 0)
        {
            return null;
        }

        foreach (var (name, minimum) in GetToolFrameworks())
        {
            var existing = frameworks.FirstOrDefault(f => string.Equals((string)f["name"], name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                var added = new JsonObject { ["name"] = name, ["version"] = minimum.ToString(3) };
                if (options["frameworks"] is not JsonArray list)
                {
                    list = [];
                    if (options["framework"] is JsonObject moved)
                    {
                        options.Remove("framework");
                        list.Add(moved);
                    }

                    options["frameworks"] = list;
                }

                list.Add(added);
                frameworks.Add(added);
            }
            else if (!Version.TryParse((string)existing["version"], out var current) || current < minimum)
            {
                existing["version"] = minimum.ToString(3);
            }
        }

        var path = Path.Combine(Path.GetTempPath(), $"asyncapi-saunter-{Guid.NewGuid():N}.runtimeconfig.json");
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    private static IEnumerable<(string Name, Version Version)> GetToolFrameworks()
    {
        var toolRuntimeConfig = Path.ChangeExtension(typeof(AppContextLauncher).Assembly.Location, ".runtimeconfig.json");
        if (File.Exists(toolRuntimeConfig)
            && JsonNode.Parse(File.ReadAllText(toolRuntimeConfig))?["runtimeOptions"] is JsonObject options)
        {
            var nodes = options["frameworks"] is JsonArray many ? many.OfType<JsonObject>() : [options["framework"] as JsonObject];
            var result = nodes
                .Where(n => n is not null && Version.TryParse((string)n["version"], out _))
                .Select(n => ((string)n["name"], Version.Parse((string)n["version"])))
                .ToList();
            if (result.Count > 0)
            {
                return result;
            }
        }

        // Fall back to the target framework the tool was compiled for (it references Microsoft.AspNetCore.App).
        var tfm = typeof(AppContextLauncher).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        var version = tfm is null ? null : new FrameworkName(tfm).Version;
        return version is null
            ? []
            : [("Microsoft.NETCore.App", new Version(version.Major, version.Minor, 0)), ("Microsoft.AspNetCore.App", new Version(version.Major, version.Minor, 0))];
    }

    private static string GetDotnetHostPath()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not null && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath))
        {
            return hostPath;
        }

        // <dotnet root>/shared/Microsoft.NETCore.App/<version>/
        var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(candidate) ? candidate : "dotnet";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
