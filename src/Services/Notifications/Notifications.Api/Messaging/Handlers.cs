using HelpDeskFlow.Contracts;
using Microsoft.EntityFrameworkCore;
using Notifications.Api.Data;
using Notifications.Api.Domain;
using Npgsql;

namespace Notifications.Api.Messaging;

/// <summary>
/// Grava notificações de forma IDEMPOTENTE: o registro "este evento já foi tratado por este handler" e as
/// notificações são salvos na MESMA transação. Se o RabbitMQ entregar o evento duas vezes, a segunda é ignorada.
/// </summary>
public class NotificationWriter(NotificationsDbContext db, ILogger<NotificationWriter> logger)
{
    public async Task<bool> TryWriteAsync(Guid eventId, string handler, IReadOnlyList<Notification> notifications, CancellationToken ct)
    {
        if (await db.ProcessedMessages.AnyAsync(p => p.EventId == eventId && p.Handler == handler, ct))
        {
            logger.LogInformation("Evento {EventId} já tratado por {Handler}; ignorando duplicata.", eventId, handler);
            return false;
        }

        db.ProcessedMessages.Add(ProcessedMessage.Create(eventId, handler));
        db.Notifications.AddRange(notifications);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Duas cópias do mesmo evento processadas ao mesmo tempo: o índice único decidiu quem ganhou.
            logger.LogInformation("Evento {EventId} tratado em paralelo por outra instância; ignorando.", eventId);
            return false;
        }

        foreach (var n in notifications)
            logger.LogInformation("[E-MAIL SIMULADO] para {Email} | {Subject}", n.RecipientEmail, n.Subject);

        return true;
    }
}

public class UserRegisteredHandler(NotificationsDbContext db) : IEventHandler<UserRegistered>
{
    public async Task HandleAsync(UserRegistered e, CancellationToken ct)
    {
        var existing = await db.KnownUsers.FindAsync([e.UserId], ct);
        if (existing is null)
            db.KnownUsers.Add(KnownUser.Create(e.UserId, e.TenantId, e.Name, e.Email, e.Role));
        else
            existing.Update(e.Name, e.Email, e.Role);

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Chamado novo: avisa toda a equipe (Admin e Agent) da empresa.</summary>
public class TicketCreatedHandler(NotificationsDbContext db, NotificationWriter writer) : IEventHandler<TicketCreated>
{
    public async Task HandleAsync(TicketCreated e, CancellationToken ct)
    {
        var staff = await db.KnownUsers.AsNoTracking()
            .Where(u => u.TenantId == e.TenantId && (u.Role == "Admin" || u.Role == "Agent"))
            .ToListAsync(ct);

        var notifications = staff
            .Select(u => Notification.Create(e.TenantId, u,
                $"Novo chamado: {e.Title}", $"Um chamado de prioridade {e.Priority} foi aberto: \"{e.Title}\"."))
            .ToList();

        await writer.TryWriteAsync(e.EventId, nameof(TicketCreatedHandler), notifications, ct);
    }
}

/// <summary>Chamado atribuído: avisa o responsável e quem abriu o chamado.</summary>
public class TicketAssignedHandler(NotificationsDbContext db, NotificationWriter writer, ILogger<TicketAssignedHandler> logger)
    : IEventHandler<TicketAssigned>
{
    public async Task HandleAsync(TicketAssigned e, CancellationToken ct)
    {
        var ids = new[] { e.AssigneeId, e.RequesterId };
        var users = await db.KnownUsers.AsNoTracking()
            .Where(u => u.TenantId == e.TenantId && ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var notifications = new List<Notification>();

        if (users.TryGetValue(e.AssigneeId, out var assignee))
            notifications.Add(Notification.Create(e.TenantId, assignee,
                $"Chamado atribuído a você: {e.Title}", $"O chamado \"{e.Title}\" agora é sua responsabilidade."));
        else
            logger.LogWarning("Responsável {UserId} ainda desconhecido; sem notificação para ele.", e.AssigneeId);

        if (users.TryGetValue(e.RequesterId, out var requester) && e.RequesterId != e.AssigneeId)
            notifications.Add(Notification.Create(e.TenantId, requester,
                $"Seu chamado está em atendimento: {e.Title}",
                $"{assignee?.Name ?? "Um atendente"} assumiu o seu chamado \"{e.Title}\"."));

        await writer.TryWriteAsync(e.EventId, nameof(TicketAssignedHandler), notifications, ct);
    }
}

/// <summary>Chamado resolvido: avisa quem abriu.</summary>
public class TicketResolvedHandler(NotificationsDbContext db, NotificationWriter writer) : IEventHandler<TicketResolved>
{
    public async Task HandleAsync(TicketResolved e, CancellationToken ct)
    {
        var requester = await db.KnownUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.TenantId == e.TenantId && u.Id == e.RequesterId, ct);

        // Se o solicitante ainda não é conhecido (UserRegistered atrasou), lançar uma exceção faz o consumidor
        // tentar de novo; se persistir, a mensagem vai para a DLQ, onde fica visível em vez de sumir em silêncio.
        if (requester is null)
            throw new InvalidOperationException($"Solicitante {e.RequesterId} desconhecido para o evento {e.EventId}.");

        var notifications = new List<Notification>
        {
            Notification.Create(e.TenantId, requester,
                $"Chamado resolvido: {e.Title}", $"O seu chamado \"{e.Title}\" foi marcado como resolvido.")
        };

        await writer.TryWriteAsync(e.EventId, nameof(TicketResolvedHandler), notifications, ct);
    }
}
