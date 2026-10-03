using HelpDeskFlow.Observability;
using HelpDeskFlow.Auth;
using HelpDeskFlow.Messaging.RabbitMq;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Tickets.Api;
using Tickets.Application;
using Tickets.Application.Abstractions;
using Tickets.Domain;
using Tickets.Infrastructure;
using Tickets.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Logs estruturados + traces + métricas + health checks (building block compartilhado).
builder.AddHelpDeskObservability("tickets");
builder.Services.AddHealthChecks()
    .AddDbContextCheck<TicketsDbContext>("postgres", tags: ["ready"])
    .AddRabbitMqCheck();

builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false); // não anuncia tecnologia/versão
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHelpDeskAuthentication(builder.Configuration);
builder.Services.AddRabbitMessaging(builder.Configuration, serviceName: "tickets");
builder.Services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
builder.Services.AddTicketsServices(builder.Configuration);
builder.Services.AddProblemDetails();

// Enums trafegam como texto ("High") em vez de número (2): mais legível e estável para clientes.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

// Modo "só migrar": usado como initContainer/Job no Kubernetes. Aplica as migrations e ENCERRA com sucesso, sem subir o
// servidor nem os consumidores. Várias réplicas podem rodar isto ao mesmo tempo: o EF Core usa um lock no banco.
if (args.Contains("--migrate-only"))
{
    using var migrationScope = app.Services.CreateScope();
    await migrationScope.ServiceProvider.GetRequiredService<TicketsDbContext>().Database.MigrateAsync();
    return;
}
app.UseHelpDeskObservability();

// Converte exceções conhecidas em respostas HTTP corretas (400) em vez de 500.
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = error switch
    {
        DomainException => (StatusCodes.Status400BadRequest, "Regra de negócio violada"),
        ForbiddenException => (StatusCodes.Status403Forbidden, "Ação não permitida"),
        InvalidTokenClaimsException => (StatusCodes.Status401Unauthorized, "Token inválido"),
        BadHttpRequestException => (StatusCodes.Status400BadRequest, "Requisição inválida"),
        _ => (StatusCodes.Status500InternalServerError, "Erro interno")
    };

    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new
    {
        title,
        status,
        // Só expomos mensagens que nós mesmos escrevemos; o resto pode vazar detalhes internos.
        detail = error is DomainException or ForbiddenException ? error.Message : null
    });
}));

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// Migrations ao iniciar: sempre em desenvolvimento; em contêiner/produção, só se "Database:MigrateOnStartup" = true.
// (Com várias instâncias, prefira rodar as migrations como um passo separado do deploy.)
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<TicketsDbContext>().Database.MigrateAsync();
}

app.UseAuthentication();
app.UseAuthorization();

// Todas as rotas exigem token válido. Papéis refinam quem pode o quê.
var tickets = app.MapGroup("/api/tickets").WithTags("Tickets").RequireAuthorization();

tickets.MapPost("/", async (CreateTicketRequest request, TicketService service, CancellationToken ct) =>
{
    var created = await service.CreateAsync(request, ct);
    return Results.Created($"/api/tickets/{created.Id}", created);
});

tickets.MapGet("/", (TicketService service, CancellationToken ct) => service.ListAsync(ct));

// Equipe da empresa que pode ser escolhida como responsável (Admin/Agent).
tickets.MapGet("/staff", (TicketService service, CancellationToken ct) => service.ListStaffAsync(ct))
    .RequireAuthorization(Policies.Staff);

tickets.MapGet("/{id:guid}", async (Guid id, TicketService service, CancellationToken ct) =>
    await service.GetAsync(id, ct) is { } ticket ? Results.Ok(ticket) : Results.NotFound());

// Conversa do chamado. Quem pode ver o chamado pode ler e comentar; notas internas só existem para a equipe.
tickets.MapGet("/{id:guid}/comments", async (Guid id, TicketService service, CancellationToken ct) =>
    await service.ListCommentsAsync(id, ct) is { } comments ? Results.Ok(comments) : Results.NotFound());

tickets.MapPost("/{id:guid}/comments", async (Guid id, AddCommentRequest request, TicketService service, CancellationToken ct) =>
    await service.AddCommentAsync(id, request, ct) is { } comment
        ? Results.Created($"/api/tickets/{id}/comments/{comment.Id}", comment)
        : Results.NotFound());

tickets.MapPut("/{id:guid}/assign", async (Guid id, AssignTicketRequest request, TicketService service, CancellationToken ct) =>
    await service.AssignAsync(id, request, ct) is { } t ? Results.Ok(t) : Results.NotFound())
    .RequireAuthorization(Policies.Staff);

tickets.MapPut("/{id:guid}/resolve", async (Guid id, TicketService service, CancellationToken ct) =>
    await service.ResolveAsync(id, ct) is { } t ? Results.Ok(t) : Results.NotFound())
    .RequireAuthorization(Policies.Staff);

tickets.MapPut("/{id:guid}/close", async (Guid id, TicketService service, CancellationToken ct) =>
    await service.CloseAsync(id, ct) is { } t ? Results.Ok(t) : Results.NotFound())
    .RequireAuthorization(Policies.Staff);

tickets.MapPut("/{id:guid}/reopen", async (Guid id, TicketService service, CancellationToken ct) =>
    await service.ReopenAsync(id, ct) is { } t ? Results.Ok(t) : Results.NotFound());

app.MapHelpDeskHealth();

app.Run();
