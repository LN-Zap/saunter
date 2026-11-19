using AsyncAPI.Saunter.Generator.Cli.ToFile;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

DependencyResolver.Initialize();

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddSimpleConsole(x => x.SingleLine = true).SetMinimumLevel(LogLevel.Trace));
services.AddToFileCommand();

using var serviceProvider = services.BuildServiceProvider();
var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
ConsoleApp.LogError = msg => logger.LogError(msg);
ConsoleApp.ServiceProvider = serviceProvider;

// Reset before running (the entry point may be invoked more than once in-process); the command's result or a
// failure sets the final exit code.
Environment.ExitCode = 0;

var app = ConsoleApp.Create();
app.Add<ToFileCommand>();
app.Run(args);
