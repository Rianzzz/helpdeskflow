namespace HelpDeskFlow.Contracts;

// Estes records são o "contrato público" entre os serviços. Mudar um campo aqui pode quebrar
// consumidores: prefira SEMPRE adicionar campos novos em vez de renomear ou remover.

/// <summary>Publicado pelo Identity quando um usuário é criado (inclusive o Admin de uma nova empresa).</summary>
public record UserRegistered(
    Guid EventId, DateTime OccurredAt, Guid TenantId, Guid UserId, string Name, string Email, string Role)
    : IIntegrationEvent
{
    public static string EventName => "identity.user-registered";

    public static UserRegistered Create(Guid tenantId, Guid userId, string name, string email, string role) =>
        new(Guid.NewGuid(), DateTime.UtcNow, tenantId, userId, name, email, role);
}

/// <summary>Publicado pelo Tickets.</summary>
public record TicketCreated(
    Guid EventId, DateTime OccurredAt, Guid TenantId, Guid TicketId, Guid RequesterId, string Title, string Priority)
    : IIntegrationEvent
{
    public static string EventName => "tickets.ticket-created";

    public static TicketCreated Create(Guid tenantId, Guid ticketId, Guid requesterId, string title, string priority) =>
        new(Guid.NewGuid(), DateTime.UtcNow, tenantId, ticketId, requesterId, title, priority);
}

public record TicketAssigned(
    Guid EventId, DateTime OccurredAt, Guid TenantId, Guid TicketId, Guid RequesterId, Guid AssigneeId, string Title)
    : IIntegrationEvent
{
    public static string EventName => "tickets.ticket-assigned";

    public static TicketAssigned Create(Guid tenantId, Guid ticketId, Guid requesterId, Guid assigneeId, string title) =>
        new(Guid.NewGuid(), DateTime.UtcNow, tenantId, ticketId, requesterId, assigneeId, title);
}

public record TicketResolved(
    Guid EventId, DateTime OccurredAt, Guid TenantId, Guid TicketId, Guid RequesterId, Guid ResolvedBy, string Title)
    : IIntegrationEvent
{
    public static string EventName => "tickets.ticket-resolved";

    public static TicketResolved Create(Guid tenantId, Guid ticketId, Guid requesterId, Guid resolvedBy, string title) =>
        new(Guid.NewGuid(), DateTime.UtcNow, tenantId, ticketId, requesterId, resolvedBy, title);
}
