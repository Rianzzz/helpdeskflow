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

/// <summary>
/// Utilitário para ids de evento DETERMINÍSTICOS: o mesmo fato de negócio sempre gera o mesmo EventId.
/// Assim, mesmo que duas instâncias de um serviço detectem o mesmo fato ao mesmo tempo, o Outbox (chave primária)
/// e os consumidores (idempotência por EventId) tratam como UM evento só.
/// </summary>
public static class EventIds
{
    public static Guid FromKey(string key) =>
        new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))[..16]);
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

/// <summary>
/// Alguém comentou num chamado. NÃO leva o texto do comentário (privacidade: o conteúdo fica só no serviço Tickets e
/// é buscado pela API com a autorização de quem lê). Leva o necessário para decidir QUEM avisar; em especial,
/// <see cref="IsInternal"/>: notas internas jamais podem gerar aviso para quem abriu o chamado.
/// </summary>
public record TicketCommented(
    Guid EventId, DateTime OccurredAt, Guid TenantId, Guid TicketId, Guid CommentId,
    Guid AuthorId, string AuthorName, Guid RequesterId, Guid? AssigneeId, string Title, bool IsInternal) : IIntegrationEvent
{
    public static string EventName => "tickets.ticket-commented";

    public static TicketCommented Create(
        Guid tenantId, Guid ticketId, Guid commentId, Guid authorId, string authorName,
        Guid requesterId, Guid? assigneeId, string title, bool isInternal) =>
        new(EventIds.FromKey($"ticket-commented:{commentId}"), DateTime.UtcNow, tenantId, ticketId, commentId,
            authorId, authorName, requesterId, assigneeId, title, isInternal);
}

/// <summary>
/// Chamado aberto e sem responsável além do prazo (SLA) da sua prioridade. Detectado por um job agendado.
/// EventId determinístico por chamado: o alerta de um mesmo chamado nunca é duplicado.
/// </summary>
public record TicketSlaBreached(
    Guid EventId, DateTime OccurredAt, Guid TenantId, Guid TicketId, Guid RequesterId,
    string Title, string Priority, int MinutesWaiting) : IIntegrationEvent
{
    public static string EventName => "tickets.ticket-sla-breached";

    public static TicketSlaBreached Create(
        Guid tenantId, Guid ticketId, Guid requesterId, string title, string priority, int minutesWaiting) =>
        new(EventIds.FromKey($"sla-breached:{ticketId}"), DateTime.UtcNow, tenantId, ticketId, requesterId,
            title, priority, minutesWaiting);
}

// ───────────────────────── Saga de onboarding de empresa ─────────────────────────
// Identity (cria a empresa "em provisionamento") → Tenants (valida e cria o perfil/plano) → Identity (ativa ou compensa).

/// <summary>Identity → Tenants: uma empresa se cadastrou e aguarda provisionamento. NÃO carrega senha nem hash.</summary>
public record TenantRegistered(
    Guid EventId, DateTime OccurredAt, Guid TenantId, string CompanyName,
    Guid AdminUserId, string AdminName, string AdminEmail) : IIntegrationEvent
{
    public static string EventName => "identity.tenant-registered";

    public static TenantRegistered Create(Guid tenantId, string companyName, Guid adminUserId, string adminName, string adminEmail) =>
        new(EventIds.FromKey($"tenant-registered:{tenantId}"), DateTime.UtcNow, tenantId, companyName, adminUserId, adminName, adminEmail);
}

/// <summary>
/// Tenants → Identity: perfil e plano criados; a empresa pode ser ativada. Leva o LIMITE de usuários do plano: o Tenants
/// é o dono do plano, e o Identity (que cadastra os usuários) o aplica sem precisar consultar ninguém a cada cadastro.
/// </summary>
public record TenantProvisioned(Guid EventId, DateTime OccurredAt, Guid TenantId, string Plan, int MaxUsers) : IIntegrationEvent
{
    public static string EventName => "tenants.tenant-provisioned";

    public static TenantProvisioned Create(Guid tenantId, string plan, int maxUsers) =>
        new(EventIds.FromKey($"tenant-provisioned:{tenantId}"), DateTime.UtcNow, tenantId, plan, maxUsers);
}

/// <summary>Tenants → Identity/Notifications: o provisionamento foi recusado; é preciso COMPENSAR (desfazer) o cadastro.</summary>
public record TenantProvisioningFailed(
    Guid EventId, DateTime OccurredAt, Guid TenantId, string CompanyName,
    Guid AdminUserId, string AdminName, string AdminEmail, string Reason) : IIntegrationEvent
{
    public static string EventName => "tenants.tenant-provisioning-failed";

    public static TenantProvisioningFailed Create(
        Guid tenantId, string companyName, Guid adminUserId, string adminName, string adminEmail, string reason) =>
        new(EventIds.FromKey($"tenant-provisioning-failed:{tenantId}"), DateTime.UtcNow, tenantId, companyName,
            adminUserId, adminName, adminEmail, reason);
}

/// <summary>Identity → Notifications: a empresa foi ativada (fim feliz da saga).</summary>
public record TenantActivated(
    Guid EventId, DateTime OccurredAt, Guid TenantId, string CompanyName,
    Guid AdminUserId, string AdminName, string AdminEmail) : IIntegrationEvent
{
    public static string EventName => "identity.tenant-activated";

    public static TenantActivated Create(Guid tenantId, string companyName, Guid adminUserId, string adminName, string adminEmail) =>
        new(EventIds.FromKey($"tenant-activated:{tenantId}"), DateTime.UtcNow, tenantId, companyName, adminUserId, adminName, adminEmail);
}

/// <summary>Identity → Tenants: o prazo da saga esgotou e o Identity desistiu; quem já criou o perfil deve desfazê-lo.</summary>
public record TenantRegistrationExpired(Guid EventId, DateTime OccurredAt, Guid TenantId) : IIntegrationEvent
{
    public static string EventName => "identity.tenant-registration-expired";

    public static TenantRegistrationExpired Create(Guid tenantId) =>
        new(EventIds.FromKey($"tenant-registration-expired:{tenantId}"), DateTime.UtcNow, tenantId);
}
