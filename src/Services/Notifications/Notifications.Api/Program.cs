using HelpDeskFlow.Observability;
using System.Security.Claims;
using HelpDeskFlow.Auth;
using HelpDeskFlow.Contracts;
using HelpDeskFlow.Messaging.RabbitMq;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Notifications.Api.Data;
using Notifications.Api.Hubs;
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

// Tempo real: o servidor EMPURRA as notificações para o navegador (WebSocket, com fallback automático).
builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 4 * 1024;                  // o cliente quase não envia nada: limite pequeno
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);          // "ping" que mantém a conexão viva atrás de proxies
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(45);
    o.EnableDetailedErrors = false;                          // nunca vazar detalhes internos para o cliente
});

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
        .Select(n => new NotificationDto(n.Id, n.Subject, n.Body, n.CreatedAt, n.ReadAt))
        .ToListAsync(ct);
});

api.MapPut("/{id:guid}/read", async (
    Guid id, ClaimsPrincipal user, NotificationsDbContext db,
    IHubContext<NotificationsHub, INotificationsClient> hub, CancellationToken ct) =>
{
    var (tenantId, userId) = (user.TenantId(), user.UserId());
    var notification = await db.Notifications
        .FirstOrDefaultAsync(n => n.Id == id && n.TenantId == tenantId && n.RecipientUserId == userId, ct);
    if (notification is null) return Results.NotFound();

    notification.MarkAsRead();
    await db.SaveChangesAsync(ct);

    // Avisa as OUTRAS abas/dispositivos da mesma pessoa, para o contador do menu acompanhar em tempo real.
    await hub.Clients.Group(NotificationsHub.GroupFor(tenantId, userId)).NotificationRead(id);
    return Results.NoContent();
});

// Hub de tempo real. CloseOnAuthenticationExpiration: quando o JWT expira, o servidor ENCERRA a conexão (sem isso, uma
// conexão aberta continuaria recebendo dados para sempre com um token já vencido). O cliente reconecta com um token novo.
app.MapHub<NotificationsHub>("/hubs/notifications", o => o.CloseOnAuthenticationExpiration = true)
    .RequireAuthorization();

app.MapHelpDeskHealth();

app.Run();

static class PrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(AppClaims.Subject)!);
    public static Guid TenantId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(AppClaims.TenantId)!);
}
