using CycleGuard.Api.Api;
using CycleGuard.Api.Configuration;
using CycleGuard.Api.Data;
using CycleGuard.Api.Demo;
using CycleGuard.Api.Downstream;
using CycleGuard.Api.Queue;
using CycleGuard.Api.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CycleGuardOptions>(builder.Configuration.GetSection(CycleGuardOptions.SectionName));

// TimeProvider is injected everywhere rather than calling DateTime.UtcNow, so the whole
// engine can be driven by FakeTimeProvider in tests.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<SimulationState>(provider =>
{
    var options = provider.GetRequiredService<IOptions<CycleGuardOptions>>().Value;
    return new SimulationState { TimeScale = options.Demo.TimeScale };
});

builder.Services.AddDbContextFactory<CycleGuardDbContext>((provider, dbOptions) =>
{
    var options = provider.GetRequiredService<IOptions<CycleGuardOptions>>().Value;
    var environment = provider.GetRequiredService<IHostEnvironment>();

    var path = Path.IsPathRooted(options.DatabasePath)
        ? options.DatabasePath
        : Path.Combine(environment.ContentRootPath, options.DatabasePath);

    dbOptions.UseSqlite($"Data Source={path}");

    // busy_timeout has to be set per connection, so it goes through an interceptor rather
    // than the connection string.
    dbOptions.AddInterceptors(new SqlitePragmaInterceptor());
});

builder.Services.AddSingleton<OutageRegistry>();
builder.Services.AddSingleton<RawErrorVault>();
builder.Services.AddSingleton<IDownstreamGateway, DownstreamSimulator>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<JobExecutor>();
builder.Services.AddSingleton<JobReadService>();
builder.Services.AddSingleton<ScenarioSimulator>();
builder.Services.AddSingleton<MorningReportStore>();

builder.Services.AddHostedService<WorkerPoolService>();
builder.Services.AddHostedService<LeaseReaperService>();
builder.Services.AddHostedService<HealthMonitorService>();

builder.Services.AddOpenApi();

builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CycleGuardDbContext>>();
    await using var dbContext = await factory.CreateDbContextAsync();
    await DatabaseBootstrapper.InitialiseAsync(dbContext);
}

app.UseCors();

app.MapOpenApi();
app.UseSwaggerUI(ui =>
{
    ui.SwaggerEndpoint("/openapi/v1.json", "CycleGuard API v1");
    ui.DocumentTitle = "CycleGuard API";
});

app.MapCycleGuardEndpoints();

app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();
