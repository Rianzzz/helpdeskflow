using Tickets.Domain.Entities;

namespace Tickets.Application.Abstractions;

/// <summary>Quem está fazendo a requisição atual, extraído do JWT já validado.</summary>
public interface ICurrentUser
{
    Guid TenantId { get; }
    Guid UserId { get; }

    /// <summary>Clientes só enxergam os próprios chamados; a equipe (Admin/Agent) vê todos da empresa.</summary>
    bool IsCustomer { get; }
}

/// <summary>Membro da equipe de atendimento (Admin ou Agent), para escolher o responsável de um chamado.</summary>
public record StaffMember(Guid Id, string Name, string Role);

/// <summary>Nome e papel de um usuário (da cópia local replicada do Identity), para identificar autores na conversa.</summary>
public record KnownUserInfo(string Name, string Role);

/// <summary>Ação proibida para o papel de quem pede (ex.: cliente criando nota interna). Vira HTTP 403.</summary>
public class ForbiddenException(string message) : Exception(message);

public interface IKnownUserRepository
{
    /// <summary>O usuário existe NESTA empresa e é da equipe de atendimento (Admin ou Agent)?</summary>
    Task<bool> IsStaffOfTenantAsync(Guid tenantId, Guid userId, CancellationToken ct);

    /// <summary>Nomes dos usuários pedidos (só os da empresa informada), para exibir nos chamados.</summary>
    Task<Dictionary<Guid, string>> GetNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<List<StaffMember>> ListStaffAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Nome e papel dos usuários pedidos (só os da empresa informada).</summary>
    Task<Dictionary<Guid, KnownUserInfo>> GetUsersAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct);
}

/// <summary>
/// A camada Application define O QUE precisa ser persistido;
/// a camada Infrastructure define COMO (EF Core + PostgreSQL).
/// </summary>
public interface ITicketRepository
{
    Task<Ticket?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<List<Ticket>> ListAsync(CancellationToken ct);
    Task AddAsync(Ticket ticket, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);

    Task AddCommentAsync(TicketComment comment, CancellationToken ct);

    /// <summary>
    /// Comentários do chamado em ordem cronológica. <paramref name="includeInternal"/> = false esconde as notas internas
    /// (é o que o cliente recebe). A decisão é passada de forma EXPLÍCITA, e o filtro global do DbContext a reforça.
    /// </summary>
    Task<List<TicketComment>> ListCommentsAsync(Guid ticketId, bool includeInternal, CancellationToken ct);

    // ---- Operações "do sistema" (jobs em segundo plano): enxergam TODAS as empresas, ignorando o filtro de tenant.
    // Nunca devem ser expostas por endpoints HTTP.
    Task<List<Ticket>> SystemListUnattendedAsync(DateTime createdBeforeUtc, int take, CancellationToken ct);
    Task<Ticket?> SystemGetByIdAsync(Guid id, CancellationToken ct);
}
