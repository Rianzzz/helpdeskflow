using Tickets.Domain.Entities;

namespace Tickets.Application.Abstractions;

/// <summary>Informa qual empresa (tenant) está fazendo a requisição atual.</summary>
public interface ITenantProvider
{
    Guid TenantId { get; }
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
}
