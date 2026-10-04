using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MySqlConnector;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FinanceApp.API.Infrastructure;
using FinanceApp.API.Services;
using FinanceApp.Data.Data;
using FinanceApp.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.HttpOverrides;

const string DefaultConnectionName = "DefaultConnection";
var defaultMySqlVersion = new Version(8, 0, 46);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Configure forwarded headers so RemoteIpAddress is populated when behind a reverse proxy
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Clear known networks/proxies to allow headers from any proxy (adjust for production security)
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy
            .WithOrigins(
                "https://vmd205358.contaboserver.net",
                "http://localhost:5173",
                "http://localhost:3000"
            )
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.Converters.Add(new UtcDateTimeJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new UtcNullableDateTimeJsonConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "FinanceApp API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Введите JWT токен"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var connectionString = builder.Configuration.GetConnectionString(DefaultConnectionName)
    ?? throw new InvalidOperationException(
        $"Connection string '{DefaultConnectionName}' is not configured. Define it in appsettings.json or via ConnectionStrings__{DefaultConnectionName}.");
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("JWT signing key is not configured. Define it in appsettings.json or via Jwt__Key.");

var configuredMySqlVersion = builder.Configuration["Database:MySqlVersion"];
Version? parsedMySqlVersion = null;
if (!string.IsNullOrWhiteSpace(configuredMySqlVersion) &&
    !Version.TryParse(configuredMySqlVersion, out parsedMySqlVersion))
{
    throw new InvalidOperationException("Database:MySqlVersion must be a valid version string such as '8.0.46'.");
}

var serverVersion = new MySqlServerVersion(parsedMySqlVersion ?? defaultMySqlVersion);

var dbConnectionStringBuilder = new MySqlConnectionStringBuilder(connectionString);
NormalizeMySqlServerHost(dbConnectionStringBuilder);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(
        dbConnectionStringBuilder.ConnectionString,
        serverVersion,
        mySqlOptions => mySqlOptions.EnableRetryOnFailure()));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("AuthLogin", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("YahooSession")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    });
builder.Services.Configure<FinnhubOptions>(builder.Configuration.GetSection("Finnhub"));
builder.Services.Configure<YahooFinanceOptions>(builder.Configuration.GetSection("YahooFinance"));
builder.Services.AddHttpClient<IFinnhubQuoteService, FinnhubQuoteService>(client =>
{
    client.BaseAddress = new Uri("https://finnhub.io/api/v1/");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
});
builder.Services.Configure<FinanzenNetOptions>(builder.Configuration.GetSection("FinanzenNet"));
builder.Services.AddSingleton<IFinanzenNetQuoteService, FinanzenNetQuoteService>();
builder.Services.AddSingleton<IYahooRequestCoordinator, YahooRequestCoordinator>();
builder.Services.AddSingleton<IYahooSessionService, YahooSessionService>();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<IUserSecurityService, UserSecurityService>();
builder.Services.AddScoped<IExchangeRateService, FrankfurterExchangeRateService>();
builder.Services.AddScoped<IYahooQuoteService, YahooQuoteService>();
builder.Services.AddScoped<IYahooFundamentalsService, YahooFundamentalsService>();
builder.Services.AddScoped<IStockQuoteConversionService, StockQuoteConversionService>();
builder.Services.AddScoped<StockQuoteSnapshotPersistenceService>();
builder.Services.AddScoped<IStockHistoryService, StockHistoryService>();
builder.Services.AddScoped<IStockDependencyDiagnosticsService, StockDependencyDiagnosticsService>();
builder.Services.AddScoped<IStockPerformanceCalculationService, StockPerformanceCalculationService>();
builder.Services.AddScoped<IFundamentalsService, FundamentalsService>();
builder.Services.AddScoped<IMarketIndexHistoryService, MarketIndexHistoryService>();
builder.Services.AddScoped<IDjiaIndexConstituentsProvider, DowJonesIndustrialAverageConstituentsProvider>();
builder.Services.AddScoped<INasdaq100IndexConstituentsProvider, Nasdaq100ConstituentsProvider>();
builder.Services.AddScoped<ISp500IndexConstituentsProvider, Sp500ConstituentsProvider>();
builder.Services.AddScoped<IDaxIndexConstituentsProvider, DaxConstituentsProvider>();
builder.Services.AddScoped<IUnsupportedIndexConstituentsProvider, YahooIndexConstituentsProvider>();
builder.Services.AddScoped<IIndexConstituentsProvider, IndexConstituentsProviderRouter>();
builder.Services.Configure<IndexConstituentHistoryRefreshJobOptions>(options => { });
builder.Services.AddSingleton<IndexConstituentHistoryRefreshJobService>();
builder.Services.AddSingleton<IIndexConstituentHistoryRefreshJobService>(sp =>
    sp.GetRequiredService<IndexConstituentHistoryRefreshJobService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexConstituentHistoryRefreshJobService>());
builder.Services.Configure<IndexConstituentsBatchQuoteRefreshJobOptions>(
    builder.Configuration.GetSection("IndexConstituentsBatchQuoteRefreshJob"));
builder.Services.AddSingleton<IndexConstituentsBatchQuoteRefreshJobService>();
builder.Services.AddSingleton<IIndexConstituentsBatchQuoteRefreshJobService>(sp =>
    sp.GetRequiredService<IndexConstituentsBatchQuoteRefreshJobService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexConstituentsBatchQuoteRefreshJobService>());
builder.Services.AddScoped<IStockQuoteFetchService, StockQuoteFetchService>();
builder.Services.Configure<CatalogStockRefreshJobOptions>(
    builder.Configuration.GetSection("CatalogStockRefreshJob"));
builder.Services.Configure<StockHistoryRefreshOptions>(
    builder.Configuration.GetSection("StockHistoryRefresh"));
builder.Services.Configure<StockQuoteRefreshOptions>(
    builder.Configuration.GetSection("StockQuoteRefresh"));
builder.Services.Configure<CatalogFundamentalsRefreshJobOptions>(
    builder.Configuration.GetSection("CatalogFundamentalsRefreshJob"));
builder.Services.Configure<StockMetadataEnrichmentOptions>(
    builder.Configuration.GetSection("StockMetadataEnrichment"));
builder.Services.Configure<SystemProcessJournalOptions>(
    builder.Configuration.GetSection("SystemProcessJournal"));
builder.Services.AddScoped<ITechnicalAnalysisSourceResolver, TechnicalAnalysisSourceResolver>();
builder.Services.AddScoped<IStockTechnicalAnalysisService, StockTechnicalAnalysisService>();
builder.Services.AddSingleton<IStockMetadataCuratedSnapshotService, StockMetadataCuratedSnapshotService>();
builder.Services.AddSingleton<IYahooAssetProfileService, YahooAssetProfileService>();
builder.Services.AddSingleton<StockMetadataEnrichmentService>();
builder.Services.AddSingleton<IStockMetadataEnrichmentService>(sp =>
    sp.GetRequiredService<StockMetadataEnrichmentService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<StockMetadataEnrichmentService>());
builder.Services.AddSingleton<ICatalogMaintenanceLeaseService, CatalogMaintenanceLeaseService>();
builder.Services.AddSingleton<ISystemProcessJournalService, SystemProcessJournalService>();
builder.Services.AddHostedService<SystemProcessJournalMaintenanceService>();
builder.Services.AddSingleton<CatalogStockRefreshHostedService>();
builder.Services.AddSingleton<ICatalogStockRefreshStatusService>(sp =>
    sp.GetRequiredService<CatalogStockRefreshHostedService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CatalogStockRefreshHostedService>());
builder.Services.AddSingleton<CatalogFundamentalsRefreshHostedService>();
builder.Services.AddSingleton<ICatalogFundamentalsRefreshStatusService>(sp =>
    sp.GetRequiredService<CatalogFundamentalsRefreshHostedService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CatalogFundamentalsRefreshHostedService>());
builder.Services.AddHostedService<StockHistoryRefreshHostedService>();
builder.Services.AddHostedService<StockQuoteRefreshHostedService>();
builder.Services.AddScoped<IRabbitMqService, RabbitMqService>();

var app = builder.Build();

app.Logger.LogInformation(
    "Using application content root {ContentRootPath} for backend configuration.",
    app.Environment.ContentRootPath);
app.Logger.LogDebug(
    "Configured MariaDB connection for server {Server} and database {Database}.",
    dbConnectionStringBuilder.Server,
    dbConnectionStringBuilder.Database);

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "FinanceApp API v1");
    c.RoutePrefix = "swagger";
});

// Handle OPTIONS preflight before routing
app.Use(async (context, next) =>
{
    if (context.Request.Method == "OPTIONS")
    {
        var origin = context.Request.Headers["Origin"].ToString();
        var allowedOrigins = new[]
        {
            "https://vmd205358.contaboserver.net",     
            "http://localhost:5173",
            "http://localhost:3000"
        };
        if (allowedOrigins.Contains(origin))
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = origin;
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, PATCH, DELETE, OPTIONS";
            context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
            context.Response.Headers["Access-Control-Max-Age"] = "86400";
        }
        context.Response.StatusCode = 204;
        return;
    }
    await next();
});

// Apply forwarded headers before routing and other middleware that depends on connection info
app.UseForwardedHeaders();

app.UseRouting();
app.UseRateLimiter();
app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

static void NormalizeMySqlServerHost(MySqlConnectionStringBuilder connectionStringBuilder)
{
    if (string.Equals(connectionStringBuilder.Server, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        connectionStringBuilder.Server = "127.0.0.1";
    }
}
