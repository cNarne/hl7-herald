using Jacobian.Hl7Integration.Worker.Extensions;
using Microsoft.Extensions.Hosting;

// Top-level statement entry point for the Worker Service host.
// Using HostApplicationBuilder (preferred over Host.CreateDefaultBuilder in .NET 8+):
//   - Strongly-typed configuration via IConfiguration / IOptions
//   - Serilog / structured logging can be wired before Build()
//   - appsettings.json + appsettings.{Environment}.json loaded by default
//   - Environment variables and command-line args are also included by default

var builder = Host.CreateApplicationBuilder(args);

// Register all HL7 worker services: parser, hosted service, and validated configuration.
// NOTE: IOrderRepository must also be registered before Run() — see the comment in
// Jacobian.Hl7Integration.Worker.Extensions.ServiceCollectionExtensions for details.
// Add the concrete implementation here once the persistence layer is implemented:
//   builder.Services.AddScoped<IOrderRepository, SqlOrderRepository>();
builder.Services.AddHl7Worker(builder.Configuration);

// On Windows, UseWindowsService() installs and manages the process as a Windows Service.
// On Linux, UseSystemd() integrates with systemd for similar lifecycle management.
// Both calls are no-ops on their non-target platforms, so it is safe to include both.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Jacobian HL7 Ingestion";
});

var host = builder.Build();
await host.RunAsync();
