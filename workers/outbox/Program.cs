using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenConnect.Core;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Transport.Nats;
using OpenConnect.WorkerHosts.Outbox;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOpenConnect();
builder.Services.AddOptions<OutboxWorkerOptions>()
    .Bind(builder.Configuration.GetSection(OutboxWorkerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<OutboxWorkerOptions>, OutboxWorkerOptionsValidator>();

var provider = builder.Configuration.GetValue<string>($"{OutboxWorkerOptions.SectionName}:DatabaseProvider") ?? "npgsql";
var connectionString = builder.Configuration.GetValue<string>($"{OutboxWorkerOptions.SectionName}:ConnectionString") ?? string.Empty;
if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddDbContext<OpenConnectDbContext>(options => options.UseSqlite(connectionString));
else if (string.Equals(provider, "npgsql", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddDbContext<OpenConnectDbContext>(options => options.UseNpgsql(connectionString));
else
    throw new InvalidOperationException("outbox_db_provider_invalid: DatabaseProvider must be npgsql or sqlite.");

builder.Services.AddOpenConnectEntityFramework<OpenConnectDbContext>(ef =>
{
    ef.Schema = "openconnect";
    ef.Migrations = OpenConnectMigrationMode.Manual;
    ef.ValidateSchema = OpenConnectSchemaValidationMode.ValidateModel;
});

var natsUrl = builder.Configuration.GetValue<string>($"{OutboxWorkerOptions.SectionName}:NatsUrl") ?? "nats://localhost:4222";
builder.Services.AddSingleton(_ => new NatsJetStreamPublisher(natsUrl, "openconnect-outbox-host"));
builder.Services.AddSingleton<IJetStreamPublisher>(services => services.GetRequiredService<NatsJetStreamPublisher>());
builder.Services.AddSingleton(services => new NatsPublisher(services.GetRequiredService<IJetStreamPublisher>()));
builder.Services.AddScoped<IOutboxRelaySource, OutboxStoreRelaySource>();
builder.Services.AddScoped<OutboxRelay>();
builder.Services.AddHostedService<OutboxDispatcherService>();

var host = builder.Build();
host.Run();
