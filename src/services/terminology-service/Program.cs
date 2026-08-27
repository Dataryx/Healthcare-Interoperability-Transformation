using CHO.TerminologyService.Configuration;
using CHO.TerminologyService.Data;
using CHO.TerminologyService.Services;
using CHO.TerminologyService.Services.CodeSystemCatalog;
using CHO.TerminologyService.Services.Loaders;
using CHO.TerminologyService.Services.Rules;
using MongoDB.Driver;
using Serilog;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.WithProperty("Service", "terminology-service")
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Service} | {Message:lj}{NewLine}{Exception}"));

var terminologyOptions = builder.Configuration
    .GetSection(TerminologyServiceOptions.SectionName)
    .Get<TerminologyServiceOptions>() ?? new TerminologyServiceOptions();

builder.Services.Configure<TerminologyServiceOptions>(
    builder.Configuration.GetSection(TerminologyServiceOptions.SectionName));

builder.Services.AddSingleton<IMongoClient>(sp =>
    new MongoClient(terminologyOptions.MongoConnectionString));

builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>()
        .GetDatabase(terminologyOptions.MongoDatabaseName));

builder.Services.AddSingleton<IConceptMapRepository, MongoConceptMapRepository>();
builder.Services.AddSingleton<ICodeSystemCatalogRepository, MongoCodeSystemCatalogRepository>();
builder.Services.AddSingleton<IContextRuleEngine, ContextRuleEngine>();
builder.Services.AddSingleton<ITerminologyTranslationService, TerminologyTranslationService>();
builder.Services.AddHostedService<CodeSystemCatalogSeedService>();

builder.Services.AddSingleton<IMapLoader, Rf2MapLoader>();
builder.Services.AddSingleton<IMapLoader, CsvMapLoader>();

builder.Services.AddMemoryCache();
builder.Services.AddMapSyndication(); // loads maps at startup, then polls for updates

// Stay on FHIR JSON here. The shared platform options serialize enums as
// strings; ConceptMap/$translate expects numeric coding + camelCase omit-null.
// See docs/architecture/shared-json-options.md.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "CHO Terminology Service",
        Version = "v1",
        Description = "FHIR ConceptMap/$translate terminology crosswalk service. " +
                      "Part of Cloud Health Office — vendor-neutral healthcare payer infrastructure.",
        Contact = new Microsoft.OpenApi.Models.OpenApiContact
        {
            Name = "Aurelianware",
            Email = "markus@aurelianware.com",
            Url = new Uri("https://cloudhealthoffice.com")
        }
    });
});

// CORS for CHO portal
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

// ──────────────────────────────────────────────────────
// Pipeline
// ──────────────────────────────────────────────────────
app.UseSerilogRequestLogging();
app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

// Map auto-load on startup and scheduled update checks are handled by
// MapSyndicationService (registered above via AddMapSyndication()).
// It runs as a BackgroundService: loads mounted files on startup,
// then checks NLM for new editions daily.

Log.Information("CHO Terminology Service started on {Urls}", string.Join(", ", app.Urls));
app.Run();
