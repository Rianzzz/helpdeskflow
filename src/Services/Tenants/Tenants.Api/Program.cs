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

app.UseExceptionHandler(h => h.Run(async ctx =>
{
    ctx.Response.StatusCode = ctx.Features.Get<IExceptionHandlerFeature>()?.Error is BadHttpRequestException
        ? StatusCodes.Status400BadRequest
        : StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new { title = "Erro", status = ctx.Response.StatusCode });
}));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
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

app.Run();
