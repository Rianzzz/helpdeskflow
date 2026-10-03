using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Tickets.Api;
using Tickets.Application;
using Tickets.Application.Abstractions;
using Tickets.Domain;
using Tickets.Infrastructure;
using Tickets.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantProvider, HttpTenantProvider>();
builder.Services.AddTicketsServices(builder.Configuration);
builder.Services.AddProblemDetails();

// Enums trafegam como texto ("High") em vez de número (2): mais legível e estável para clientes.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

// Converte exceções conhecidas em respostas HTTP corretas (400) em vez de 500.
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = error switch
    {
        DomainException => (StatusCodes.Status400BadRequest, "Regra de negócio violada"),
        TenantNotResolvedException => (StatusCodes.Status400BadRequest, "Tenant não identificado"),
        BadHttpRequestException => (StatusCodes.Status400BadRequest, "Requisição inválida"),
        _ => (StatusCodes.Status500InternalServerError, "Erro interno")
    };

    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new
    {
        title,
        status,
        // Só expomos mensagens que nós mesmos escrevemos; o resto pode vazar detalhes internos.
        detail = error is DomainException or TenantNotResolvedException ? error.Message : null
    });
}));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // Aplica as migrations ao subir (apenas em desenvolvimento).
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<TicketsDbContext>().Database.MigrateAsync();
}

var tickets = app.MapGroup("/api/tickets").WithTags("Tickets");

tickets.MapPost("/", async (CreateTicketRequest request, TicketService service, CancellationToken ct) =>
{
    var created = await service.CreateAsync(request, ct);
    return Results.Created($"/api/tickets/{created.Id}", created);
});

tickets.MapGet("/", (TicketService service, CancellationToken ct) => service.ListAsync(ct));

tickets.MapGet("/{id:guid}", async (Guid id, TicketService service, CancellationToken ct) =>
    await service.GetAsync(id, ct) is { } ticket ? Results.Ok(ticket) : Results.NotFound());

tickets.MapPut("/{id:guid}/assign", async (Guid id, AssignTicketRequest request, TicketService service, CancellationToken ct) =>
    await service.AssignAsync(id, request, ct) is { } t ? Results.Ok(t) : Results.NotFound());

tickets.MapPut("/{id:guid}/resolve", async (Guid id, TicketService service, CancellationToken ct) =>
    await service.ResolveAsync(id, ct) is { } t ? Results.Ok(t) : Results.NotFound());

tickets.MapPut("/{id:guid}/close", async (Guid id, TicketService service, CancellationToken ct) =>
    await service.CloseAsync(id, ct) is { } t ? Results.Ok(t) : Results.NotFound());

tickets.MapPut("/{id:guid}/reopen", async (Guid id, TicketService service, CancellationToken ct) =>
    await service.ReopenAsync(id, ct) is { } t ? Results.Ok(t) : Results.NotFound());

app.Run();
