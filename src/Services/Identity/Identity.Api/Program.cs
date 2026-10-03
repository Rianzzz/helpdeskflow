using HelpDeskFlow.Observability;
using System.Security.Claims;
using System.Threading.RateLimiting;
using HelpDeskFlow.Auth;
using Identity.Application;
using Identity.Domain;
using Identity.Infrastructure;
using HelpDeskFlow.Messaging.RabbitMq;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Logs estruturados + traces + métricas + health checks (building block compartilhado).
builder.AddHelpDeskObservability("identity");
builder.Services.AddHealthChecks()
    .AddDbContextCheck<IdentityDbContext>("postgres", tags: ["ready"])
    .AddRabbitMqCheck();

builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false); // não anuncia tecnologia/versão
builder.Services.AddOpenApi();
builder.Services.AddIdentityServices(builder.Configuration);
builder.Services.AddHelpDeskAuthentication(builder.Configuration);
builder.Services.AddRabbitMessaging(builder.Configuration, serviceName: "identity");
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Atrás do API Gateway, o IP de origem da conexão é o do gateway. O YARP repassa o IP real do cliente
// em X-Forwarded-For, e aqui o aceitamos SOMENTE de proxies confiáveis (por padrão, apenas loopback;
// em produção configure KnownProxies/KnownNetworks). Sem isso, o rate limit por IP contaria todo mundo junto.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

// Rate limiting: limita tentativas por IP nas rotas de autenticação (freia força bruta e abuso de cadastro).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimiting:AuthPermitPerMinute", 20),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

var app = builder.Build();
app.UseHelpDeskObservability();

app.UseForwardedHeaders();

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = error switch
    {
        DomainException => (StatusCodes.Status400BadRequest, "Dados inválidos"),
        BadHttpRequestException => (StatusCodes.Status400BadRequest, "Requisição inválida"),
        ConflictException => (StatusCodes.Status409Conflict, "Conflito"),
        AuthenticationFailedException => (StatusCodes.Status401Unauthorized, "Não autorizado"),
        _ => (StatusCodes.Status500InternalServerError, "Erro interno")
    };

    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new
    {
        title,
        status,
        // Só expomos mensagens que nós mesmos escrevemos.
        detail = error is DomainException or ConflictException or AuthenticationFailedException ? error.Message : null
    });
}));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

var auth = app.MapGroup("/api/auth").WithTags("Auth").RequireRateLimiting("auth");

// 202 Accepted: o cadastro foi RECEBIDO, mas o provisionamento acontece em segundo plano (saga).
// O cliente consulta o status e faz login quando a empresa estiver "Active".
auth.MapPost("/register-tenant", async (RegisterTenantRequest r, AuthService s, CancellationToken ct) =>
{
    var result = await s.RegisterTenantAsync(r, ct);
    return Results.Accepted($"/api/auth/tenants/{result.TenantId}/status", result);
});

auth.MapGet("/tenants/{tenantId:guid}/status", async (Guid tenantId, AuthService s, CancellationToken ct) =>
    await s.GetTenantStatusAsync(tenantId, ct) is { } status ? Results.Ok(status) : Results.NotFound());

auth.MapPost("/login", async (LoginRequest r, AuthService s, CancellationToken ct) =>
    Results.Ok(await s.LoginAsync(r, ct)));

auth.MapPost("/refresh", async (RefreshRequest r, AuthService s, CancellationToken ct) =>
    Results.Ok(await s.RefreshAsync(r, ct)));

auth.MapPost("/logout", async (RefreshRequest r, AuthService s, CancellationToken ct) =>
{
    await s.LogoutAsync(r, ct);
    return Results.NoContent();
});

var usersGroup = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization();

usersGroup.MapGet("/me", async (ClaimsPrincipal principal, AuthService s, CancellationToken ct) =>
    await s.GetUserAsync(principal.UserId(), ct) is { } me ? Results.Ok(me) : Results.NotFound());

// O tenant vem SEMPRE do token (assinado pelo servidor), nunca do corpo da requisição.
usersGroup.MapGet("/", async (ClaimsPrincipal principal, AuthService s, CancellationToken ct) =>
    Results.Ok(await s.ListUsersAsync(principal.TenantId(), ct)))
    .RequireAuthorization(Policies.AdminOnly);

usersGroup.MapPost("/", async (CreateUserRequest r, ClaimsPrincipal principal, AuthService s, CancellationToken ct) =>
{
    var created = await s.CreateUserAsync(principal.TenantId(), r, ct);
    return Results.Created($"/api/users/{created.Id}", created);
}).RequireAuthorization(Policies.AdminOnly);

app.MapHelpDeskHealth();

app.Run();

static class PrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(AppClaims.Subject)!);
    public static Guid TenantId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(AppClaims.TenantId)!);
}
