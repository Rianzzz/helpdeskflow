using HelpDeskFlow.Contracts;
using Tickets.Application.Abstractions;
using Tickets.Domain;
using Tickets.Domain.Entities;
using Tickets.Domain.Enums;
// TicketComment vive em Tickets.Domain.Entities (já importado acima)

namespace Tickets.Application;

public record CreateTicketRequest(string Title, string Description, TicketPriority Priority);
public record AssignTicketRequest(Guid AssigneeId);

public record TicketResponse(
    Guid Id, Guid RequesterId, string Title, string Description, TicketStatus Status,
    TicketPriority Priority, Guid? AssigneeId, DateTime CreatedAt, DateTime? ClosedAt, DateTime? SlaBreachedAt,
    string? RequesterName = null, string? AssigneeName = null)
{
    public static TicketResponse From(Ticket t, IReadOnlyDictionary<Guid, string>? names = null) =>
        new(t.Id, t.RequesterId, t.Title, t.Description, t.Status, t.Priority, t.AssigneeId, t.CreatedAt, t.ClosedAt, t.SlaBreachedAt,
            names?.GetValueOrDefault(t.RequesterId),
            t.AssigneeId is { } a ? names?.GetValueOrDefault(a) : null);
}

public record AddCommentRequest(string Body, bool IsInternal = false);

public record CommentResponse(
    Guid Id, Guid TicketId, Guid AuthorId, string? AuthorName, string? AuthorRole,
    string Body, bool IsInternal, DateTime CreatedAt)
{
    public static CommentResponse From(TicketComment c, IReadOnlyDictionary<Guid, KnownUserInfo>? authors = null)
    {
        KnownUserInfo? author = null;
        authors?.TryGetValue(c.AuthorId, out author);
        return new(c.Id, c.TicketId, c.AuthorId, author?.Name, author?.Role, c.Body, c.IsInternal, c.CreatedAt);
    }
}

public class TicketService(
    ITicketRepository repository,
    IKnownUserRepository knownUsers,
    ICurrentUser currentUser,
    IEventPublisher events)
{
    public async Task<TicketResponse> CreateAsync(CreateTicketRequest request, CancellationToken ct)
    {
        var ticket = Ticket.Open(currentUser.TenantId, currentUser.UserId, request.Title, request.Description, request.Priority);
        await repository.AddAsync(ticket, ct);

        // Outbox: o evento é registrado ANTES do SaveChanges e gravado na mesma transação do chamado.
        // Ou os dois existem, ou nenhum. O envio ao RabbitMQ acontece depois, em segundo plano.
        await events.PublishAsync(TicketCreated.Create(
            ticket.TenantId, ticket.Id, ticket.RequesterId, ticket.Title, ticket.Priority.ToString()));
        await repository.SaveChangesAsync(ct);

        return await ToResponseAsync(ticket, ct);
    }

    public async Task<List<TicketResponse>> ListAsync(CancellationToken ct)
    {
        var tickets = await repository.ListAsync(ct);
        var names = await NamesForAsync(tickets, ct);
        return tickets.Select(t => TicketResponse.From(t, names)).ToList();
    }

    public async Task<TicketResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        var ticket = await repository.GetByIdAsync(id, ct);
        return ticket is null ? null : await ToResponseAsync(ticket, ct);
    }

    /// <summary>
    /// Conversa do chamado. Devolve null se o chamado não existe OU não é visível para quem pede (cliente de outro
    /// chamado, outra empresa): a verificação de acesso é a do próprio chamado, que já passa pelos filtros globais.
    /// Notas internas só vêm para a equipe.
    /// </summary>
    public async Task<List<CommentResponse>?> ListCommentsAsync(Guid ticketId, CancellationToken ct)
    {
        if (await repository.GetByIdAsync(ticketId, ct) is null) return null;

        var comments = await repository.ListCommentsAsync(ticketId, includeInternal: !currentUser.IsCustomer, ct);
        var authors = await knownUsers.GetUsersAsync(currentUser.TenantId, comments.Select(c => c.AuthorId).Distinct().ToList(), ct);
        return comments.Select(c => CommentResponse.From(c, authors)).ToList();
    }

    public async Task<CommentResponse?> AddCommentAsync(Guid ticketId, AddCommentRequest request, CancellationToken ct)
    {
        var ticket = await repository.GetByIdAsync(ticketId, ct);
        if (ticket is null) return null;

        // O cliente NUNCA cria nota interna: seria inútil (ele mesmo não a veria) e mostra que a regra existe no servidor.
        if (request.IsInternal && currentUser.IsCustomer)
            throw new ForbiddenException("Somente a equipe pode criar notas internas.");

        ticket.EnsureAcceptsComments();
        var comment = TicketComment.Create(currentUser.TenantId, ticketId, currentUser.UserId, request.Body, request.IsInternal);
        await repository.AddCommentAsync(comment, ct);

        var authors = await knownUsers.GetUsersAsync(currentUser.TenantId, [currentUser.UserId], ct);
        var authorName = authors.TryGetValue(currentUser.UserId, out var info) ? info.Name : "Alguém";
        await events.PublishAsync(TicketCommented.Create(
            ticket.TenantId, ticket.Id, comment.Id, currentUser.UserId, authorName,
            ticket.RequesterId, ticket.AssigneeId, ticket.Title, comment.IsInternal));
        await repository.SaveChangesAsync(ct); // comentário + evento: uma única transação (Outbox)

        return CommentResponse.From(comment, authors);
    }

    /// <summary>Equipe que pode assumir chamados da empresa atual (para a tela de atribuição).</summary>
    public Task<List<StaffMember>> ListStaffAsync(CancellationToken ct) =>
        knownUsers.ListStaffAsync(currentUser.TenantId, ct);

    public async Task<TicketResponse?> AssignAsync(Guid id, AssignTicketRequest request, CancellationToken ct)
    {
        // Valida o responsável usando a cópia local de usuários (alimentada por eventos do Identity).
        if (!await knownUsers.IsStaffOfTenantAsync(currentUser.TenantId, request.AssigneeId, ct))
            throw new DomainException("Responsável inválido: precisa ser Admin ou Agent da sua empresa.");

        return await ChangeAsync(id, t => t.AssignTo(request.AssigneeId),
            t => events.PublishAsync(TicketAssigned.Create(t.TenantId, t.Id, t.RequesterId, request.AssigneeId, t.Title)), ct);
    }

    public Task<TicketResponse?> ResolveAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, t => t.Resolve(),
            t => events.PublishAsync(TicketResolved.Create(t.TenantId, t.Id, t.RequesterId, currentUser.UserId, t.Title)), ct);

    public Task<TicketResponse?> CloseAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, t => t.Close(), null, ct);

    public Task<TicketResponse?> ReopenAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, t => t.Reopen(), null, ct);

    private async Task<TicketResponse?> ChangeAsync(
        Guid id, Action<Ticket> change, Func<Ticket, Task>? stageEvent, CancellationToken ct)
    {
        var ticket = await repository.GetByIdAsync(id, ct);
        if (ticket is null) return null;

        change(ticket);
        if (stageEvent is not null)
            await stageEvent(ticket); // registra no Outbox, ainda sem salvar
        await repository.SaveChangesAsync(ct); // chamado + evento: uma única transação

        return await ToResponseAsync(ticket, ct);
    }

    private async Task<TicketResponse> ToResponseAsync(Ticket ticket, CancellationToken ct) =>
        TicketResponse.From(ticket, await NamesForAsync([ticket], ct));

    /// <summary>Resolve os nomes (solicitante e responsável) a partir da cópia local de usuários, numa única consulta.</summary>
    private async Task<Dictionary<Guid, string>> NamesForAsync(IEnumerable<Ticket> tickets, CancellationToken ct)
    {
        var ids = tickets
            .SelectMany(t => new[] { (Guid?)t.RequesterId, t.AssigneeId })
            .OfType<Guid>().Distinct().ToList();

        return ids.Count == 0 ? [] : await knownUsers.GetNamesAsync(currentUser.TenantId, ids, ct);
    }
}
