using Forge.ControlApi.Persistence;
using Forge.ControlApi.Services.Jobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Forge.ControlApi.Infrastructure.Messaging;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var connectionString =
    builder.Configuration.GetConnectionString("Forge")
    ?? throw new InvalidOperationException(
        "Connection string 'Forge' is not configured.");

builder.Services.AddDbContext<ForgeDbContext>(options =>
    options.UseNpgsql(connectionString));

// Application service and persistence adapter
builder.Services.AddScoped<SubmitJobService>();

builder.Services.AddScoped<ClaimJobService>();
builder.Services.AddScoped<LeaseRenewalService>();
builder.Services.AddScoped<LeaseRecoveryService>();
builder.Services.AddScoped<CompletionService>();

builder.Services.AddOptions<JobClaimOptions>()
    .Bind(builder.Configuration.GetSection(JobClaimOptions.SectionName))
    .Validate(
        options => options.LeaseDurationSeconds is >= 5 and <= 3600,
        "JobClaims:LeaseDurationSeconds must be between 5 and 3600.")
    .ValidateOnStart();

builder.Services.AddOptions<LeaseRecoveryOptions>()
    .Bind(builder.Configuration.GetSection(LeaseRecoveryOptions.SectionName))
    .Validate(
        options => options.PollIntervalMilliseconds is >= 100 and <= 60000,
        "LeaseRecovery:PollIntervalMilliseconds must be between 100 and 60000.")
    .Validate(
        options => options.BatchSize is >= 1 and <= 500,
        "LeaseRecovery:BatchSize must be between 1 and 500.")
    .Validate(
        options => options.MaxAttempts is >= 1 and <= 100,
        "LeaseRecovery:MaxAttempts must be between 1 and 100.")
    .Validate(
        options => options.InitialRetryDelaySeconds is >= 1 and <= 300,
        "LeaseRecovery:InitialRetryDelaySeconds must be between 1 and 300.")
    .Validate(
        options => options.MaxRetryDelaySeconds >= options.InitialRetryDelaySeconds
            && options.MaxRetryDelaySeconds <= 3600,
        "LeaseRecovery:MaxRetryDelaySeconds must be >= initial delay and <= 3600.")
    .Validate(
        options => options.RetryableFailureCodes is not null
            && options.RetryableFailureCodes.Count <= 100
            && options.RetryableFailureCodes.All(code =>
                !string.IsNullOrWhiteSpace(code)
                && code.Length <= 100
                && code == code.Trim()
                && code == code.ToUpperInvariant()),
        "LeaseRecovery:RetryableFailureCodes must contain at most 100 non-empty uppercase codes of up to 100 characters.")
    .ValidateOnStart();

builder.Services.AddScoped<
    IJobSubmissionStore,
    PostgresJobSubmissionStore>();
builder.Services.AddScoped<
    IOutboxMessageStore,
    PostgresOutboxMessageStore>();
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<LeaseRecoveryWorker>();

var authority =
    builder.Configuration["Authentication:Authority"];

var audience =
    builder.Configuration["Authentication:Audience"];

var authenticationConfigured =
    !string.IsNullOrWhiteSpace(authority) &&
    !string.IsNullOrWhiteSpace(audience);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;

        options.Authority = authenticationConfigured
            ? authority
            : null;

        options.Audience = authenticationConfigured
            ? audience
            : null;

        options.RequireHttpsMetadata =
            builder.Configuration.GetValue(
                "Authentication:RequireHttpsMetadata",
                true);

        options.TokenValidationParameters =
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromSeconds(60)
            };

        if (!authenticationConfigured)
        {
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    context.Fail(
                        "JWT authentication is not configured. " +
                        "Set Authentication:Authority and " +
                        "Authentication:Audience.");

                    return Task.CompletedTask;
                }
            };
        }
    });

builder.Services.AddAuthorization();
builder.Services
    .AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(
        RabbitMqOptions.SectionName))
    .Validate(
        options =>
            !string.IsNullOrWhiteSpace(options.HostName) &&
            options.Port is > 0 and <= 65535 &&
            !string.IsNullOrWhiteSpace(options.UserName) &&
            !string.IsNullOrWhiteSpace(options.Password) &&
            !string.IsNullOrWhiteSpace(options.ExchangeName) &&
            !string.IsNullOrWhiteSpace(options.QueueName) &&
            !string.IsNullOrWhiteSpace(options.RoutingKey),
        "RabbitMQ configuration is incomplete.")
    .ValidateOnStart();

builder.Services.AddSingleton<RabbitMqConnectionProvider>();

builder.Services.AddHostedService<RabbitMqTopologyInitializer>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
