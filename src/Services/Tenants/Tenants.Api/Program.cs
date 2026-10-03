using HelpDeskFlow.Observability;
using System.Security.Claims;
using HelpDeskFlow.Auth;
using HelpDeskFlow.Contracts;
using HelpDeskFlow.Messaging.Outbox;
using HelpDeskFlow.Messaging.RabbitMq;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Tenants.Api.Data;
using Tenants.Api.Messaging;

var builder = WebApplication.CreateBuilder(args);

// Logs estruturados + traces + métricas + health checks (building block compartilhado).
builder.AddHelpDeskObservability("tenants");
builder.Services.AddHealthChecks()
    .AddDbContextCheck<TenantsDbContext>("postgres", tags: ["ready"])
    .AddRabbitMqCheck();

builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);
builder.Services.AddOpenApi();
builder.Services.AddHelpDeskAuthentication(builder.Configuration);

builder.Services.AddDbContext<TenantsDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("TenantsDb")).UseSnakeCaseNamingConvention());

builder.Services.AddRabbitMessaging(builder.Configuration, serviceName: "tenants");
builder.Services.AddOutbox<TenantsDbContext>(); // eventos saem pelo Outbox, na mesma transação da decisão
builder.Services.AddEventHandler<TenantRegistered, TenantRegisteredHandler>();
builder.Services.AddEventHandler<TenantRegistrationExpired, TenantRegistrationExpiredHandler>();

var app = builder.Build();

// Modo "só migrar": usado como initContainer/Job no Kubernetes. Aplica as migrations e ENCERRA com sucesso, sem subir o
// servidor nem os consumidores. Várias réplicas podem rodar isto ao mesmo tempo: o EF Core usa um lock no banco.
if (args.Contains("--migrate-only"))
{
    using var migrationScope = app.Services.CreateScope();
    await migrationScope.ServiceProvider.GetRequiredService<TenantsDbContext>().Database.MigrateAsync();
    return;
}
app.UseHelpDeskObservability();

app.UseExceptionHandler(h => h.Run(async ctx =>
{
    ctx.Response.StatusCode = ctx.Features.Get<IExceptionHandlerFeature>()?.Error is BadHttpRequestException
        ? StatusCodes.Status400BadRequest
        : StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new { title = "Erro", status = ctx.Response.StatusCode });
}));

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// Migrations ao iniciar: sempre em desenvolvimento; em contêiner/produção, só se "Database:MigrateOnStartup" = true.
// (Com várias instâncias, prefira rodar as migrations como um passo separado do deploy.)
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<TenantsDbContext>().Database.MigrateAsync();
}

app.UseAuthentication();
app.UseAuthorization();

// Perfil da PRÓPRIA empresa (o tenant vem do token, nunca da URL).
app.MapGet("/api/tenants/me", async (ClaimsPrincipal user, TenantsDbContext db, CancellationToken ct) =>
{
    var tenantId = Guid.Parse(user.FindFirstValue(AppClaims.TenantId)!);
    var profile = await db.Profiles.AsNoTracking()
        .Where(p => p.Id == tenantId)
        .Select(p => new { p.Id, p.Name, Plan = p.Plan.ToString(), p.MaxUsers, p.CreatedAt })
        .FirstOrDefaultAsync(ct);

    return profile is null ? Results.NotFound() : Results.Ok(profile);
}).RequireAuthorization().WithTags("Tenants");

app.MapHelpDeskHealth();

app.Run();
