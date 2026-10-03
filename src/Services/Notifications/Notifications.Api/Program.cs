using HelpDeskFlow.Observability;
using System.Security.Claims;
using HelpDeskFlow.Auth;
using HelpDeskFlow.Contracts;
using HelpDeskFlow.Messaging.RabbitMq;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Notifications.Api.Data;
using Notifications.Api.Messaging;

var builder = WebApplication.CreateBuilder(args);

// Logs estruturados + traces + métricas + health checks (building block compartilhado).
builder.AddHelpDeskObservability("notifications");
builder.Services.AddHealthChecks()
    .AddDbContextCheck<NotificationsDbContext>("postgres", tags: ["ready"])
    .AddRabbitMqCheck();

builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);
builder.Services.AddOpenApi();
builder.Services.AddHelpDeskAuthentication(builder.Configuration);

builder.Services.AddDbContext<NotificationsDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("NotificationsDb")).UseSnakeCaseNamingConvention());
builder.Services.AddScoped<NotificationWriter>();

// Este serviço é "reativo": quase tudo o que faz é consumir eventos de outros serviços.
builder.Services.AddRabbitMessaging(builder.Configuration, serviceName: "notifications");
builder.Services.AddEventHandler<UserRegistered, UserRegisteredHandler>();
builder.Services.AddEventHandler<TicketCreated, TicketCreatedHandler>();
builder.Services.AddEventHandler<TicketAssigned, TicketAssignedHandler>();
builder.Services.AddEventHandler<TicketResolved, TicketResolvedHandler>();
builder.Services.AddEventHandler<TicketSlaBreached, TicketSlaBreachedHandler>();
builder.Services.AddEventHandler<TenantActivated, TenantActivatedHandler>();
builder.Services.AddEventHandler<TenantProvisioningFailed, TenantProvisioningFailedHandler>();

var app = builder.Build();
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
    await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.MigrateAsync();
}

app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization();

// Cada usuário só vê as PRÓPRIAS notificações (usuário e empresa vêm do token, nunca da URL).
api.MapGet("/", async (bool? unread, ClaimsPrincipal user, NotificationsDbContext db, CancellationToken ct) =>
{
    var (tenantId, userId) = (user.TenantId(), user.UserId());
    var query = db.Notifications.AsNoTracking()
        .Where(n => n.TenantId == tenantId && n.RecipientUserId == userId);

    if (unread == true) query = query.Where(n => n.ReadAt == null);

    return await query.OrderByDescending(n => n.CreatedAt).Take(50)
        .Select(n => new { n.Id, n.Subject, n.Body, n.CreatedAt, n.ReadAt })
        .ToListAsync(ct);
});

api.MapPut("/{id:guid}/read", async (Guid id, ClaimsPrincipal user, NotificationsDbContext db, CancellationToken ct) =>
{
    var (tenantId, userId) = (user.TenantId(), user.UserId());
    var notification = await db.Notifications
        .FirstOrDefaultAsync(n => n.Id == id && n.TenantId == tenantId && n.RecipientUserId == userId, ct);
    if (notification is null) return Results.NotFound();

    notification.MarkAsRead();
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapHelpDeskHealth();

app.Run();

static class PrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(AppClaims.Subject)!);
    public static Guid TenantId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(AppClaims.TenantId)!);
}
