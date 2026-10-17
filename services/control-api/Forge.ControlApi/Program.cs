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

builder.Services.AddScoped<
    IJobSubmissionStore,
    PostgresJobSubmissionStore>();
builder.Services.AddScoped<
    IOutboxMessageStore,
    PostgresOutboxMessageStore>();
builder.Services.AddHostedService<OutboxPublisher>();

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
