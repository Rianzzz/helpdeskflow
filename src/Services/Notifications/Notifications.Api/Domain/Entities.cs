namespace Notifications.Api.Domain;

/// <summary>Uma notificação para um usuário (ex.: "Novo chamado aberto"). Simula o envio de e-mail.</summary>
public class Notification
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid RecipientUserId { get; private set; }
    public string RecipientEmail { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    public DateTime? ReadAt { get; private set; }

    private Notification() { }

    public static Notification Create(Guid tenantId, KnownUser recipient, string subject, string body) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        RecipientUserId = recipient.Id,
        RecipientEmail = recipient.Email,
        Subject = subject,
        Body = body,
        CreatedAt = DateTime.UtcNow
    };

    public void MarkAsRead() => ReadAt ??= DateTime.UtcNow;
}

/// <summary>Cópia local dos usuários (alimentada pelo evento UserRegistered) para saber QUEM notificar e o e-mail.</summary>
public class KnownUser
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string Role { get; private set; } = string.Empty;

    private KnownUser() { }

    public static KnownUser Create(Guid id, Guid tenantId, string name, string email, string role) =>
        new() { Id = id, TenantId = tenantId, Name = name, Email = email, Role = role };

    public void Update(string name, string email, string role)
    {
        Name = name;
        Email = email;
        Role = role;
    }
}

/// <summary>
/// Registro de eventos já tratados (o "inbox"). O índice único (EventId, Handler) é o que impede uma
/// entrega duplicada do RabbitMQ de gerar notificações em dobro.
/// </summary>
public class ProcessedMessage
{
    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public string Handler { get; private set; } = string.Empty;
    public DateTime ProcessedAt { get; private set; }

    private ProcessedMessage() { }

    public static ProcessedMessage Create(Guid eventId, string handler) =>
        new() { Id = Guid.NewGuid(), EventId = eventId, Handler = handler, ProcessedAt = DateTime.UtcNow };
}
