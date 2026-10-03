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

public interface IKnownUserRepository
{
    /// <summary>O usuário existe NESTA empresa e é da equipe de atendimento (Admin ou Agent)?</summary>
    Task<bool> IsStaffOfTenantAsync(Guid tenantId, Guid userId, CancellationToken ct);
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

    // ---- Operações "do sistema" (jobs em segundo plano): enxergam TODAS as empresas, ignorando o filtro de tenant.
    // Nunca devem ser expostas por endpoints HTTP.
    Task<List<Ticket>> SystemListUnattendedAsync(DateTime createdBeforeUtc, int take, CancellationToken ct);
    Task<Ticket?> SystemGetByIdAsync(Guid id, CancellationToken ct);
}
