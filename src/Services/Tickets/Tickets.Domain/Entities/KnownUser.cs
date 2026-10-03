namespace Tickets.Domain.Entities;

/// <summary>
/// Cópia LOCAL e mínima de um usuário do serviço Identity, mantida atualizada por eventos (UserRegistered).
/// Assim o Tickets valida "esse responsável existe, é da mesma empresa e é da equipe?" sem chamar o Identity
/// a cada requisição: se o Identity cair, o Tickets continua funcionando. O preço é a consistência eventual:
/// um usuário recém-criado pode levar alguns instantes para aparecer aqui.
/// </summary>
public class KnownUser
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Role { get; private set; } = string.Empty;

    private KnownUser() { }

    public static KnownUser Create(Guid id, Guid tenantId, string name, string role) =>
        new() { Id = id, TenantId = tenantId, Name = name, Role = role };

    public void Update(string name, string role)
    {
        Name = name;
        Role = role;
    }

    public bool IsStaff => Role is "Admin" or "Agent";
}
