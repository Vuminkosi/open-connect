using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenConnect.Core;
using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Persistence.EFCore.Navigator;
using OpenConnect.Persistence.EFCore.Stores;
using OpenConnect.WorkerHosts.Operational;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOpenConnect();
builder.Services.AddOptions<OperationalWorkerOptions>()
    .Bind(builder.Configuration.GetSection(OperationalWorkerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<OperationalWorkerOptions>, OperationalWorkerOptionsValidator>();

var provider = builder.Configuration.GetValue<string>($"{OperationalWorkerOptions.SectionName}:DatabaseProvider") ?? "npgsql";
var connectionString = builder.Configuration.GetValue<string>($"{OperationalWorkerOptions.SectionName}:ConnectionString") ?? string.Empty;
if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddDbContext<OpenConnectDbContext>(options => options.UseSqlite(connectionString));
else if (string.Equals(provider, "npgsql", StringComparison.OrdinalIgnoreCase)
    || string.Equals(provider, "postgres", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddDbContext<OpenConnectDbContext>(options => options.UseNpgsql(connectionString));
else if (string.Equals(provider, "sqlserver", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddDbContext<OpenConnectDbContext>(options => options.UseSqlServer(connectionString));
else
    throw new InvalidOperationException("operational_db_provider_invalid: DatabaseProvider must be npgsql, postgres, sqlserver, or sqlite.");

builder.Services.AddOpenConnectEntityFramework<OpenConnectDbContext>(ef =>
{
    ef.Schema = "openconnect";
    ef.Migrations = OpenConnectMigrationMode.Manual;
    ef.ValidateSchema = OpenConnectSchemaValidationMode.ValidateModel;
});

var supervisorOwner = $"operational-supervisor:{Guid.NewGuid():N}";
builder.Services.AddScoped<INavigatorLeaseSeam, EfNavigatorLeaseSeam<OpenConnectDbContext>>();
builder.Services.AddScoped<EfNavigatorReceiptStore<OpenConnectDbContext>>();
builder.Services.AddScoped<IDurableOperationalStore, EfOperationalDurableStore>();
builder.Services.AddScoped<IOperationalLeaseStore>(services =>
    new DurableOperationalLeaseStore(services.GetRequiredService<IDurableOperationalStore>(), supervisorOwner));
builder.Services.AddScoped<DurableIdempotencyStore>();
builder.Services.AddScoped<OperationalNavigatorHandler>();
builder.Services.AddHostedService<OperationalSupervisorService>();

var host = builder.Build();
host.Run();
