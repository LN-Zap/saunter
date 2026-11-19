using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Saunter;
using Saunter.AsyncApiSchema.v2;
using Saunter.Attributes;

var builder = WebApplication.CreateBuilder(args);

// Fail host building if any registration cannot be satisfied, e.g. because IMeterFactory was registered by the
// framework against a different copy of System.Diagnostics.DiagnosticSource than the one this app compiled against.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

builder.Services.AddMetrics();
builder.Services.AddSingleton<OrderMetrics>();

builder.Services.AddAsyncApiSchemaGeneration(options =>
{
    options.AssemblyMarkerTypes = [typeof(OrderPublisher)];
    options.AsyncApi = new AsyncApiDocument
    {
        Info = new Info("Newer runtime assembly API", "1.0.0"),
    };
});

var app = builder.Build();
app.Run();

public sealed class OrderMetrics
{
    public OrderMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("Orders");
        Placed = meter.CreateCounter<long>("orders.placed");
    }

    public Counter<long> Placed { get; }
}

public sealed record OrderPlaced(string OrderId);

[AsyncApi]
public sealed class OrderPublisher(OrderMetrics metrics)
{
    [Channel("orders/placed")]
    [PublishOperation(typeof(OrderPlaced), Summary = "An order was placed.")]
    public void Publish(OrderPlaced message) => metrics.Placed.Add(1, new KeyValuePair<string, object>("order.id", message.OrderId));
}
