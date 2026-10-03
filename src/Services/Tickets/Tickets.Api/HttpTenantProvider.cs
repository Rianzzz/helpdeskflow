using Tickets.Application.Abstractions;

namespace Tickets.Api;

/// <summary>
/// FASE 1 (temporária): o tenant vem do header "X-Tenant-Id".
/// Na Fase 2, quando o serviço Identity existir, o tenant passará a vir de uma claim
/// dentro do JWT, assinada pelo servidor — um header qualquer pode ser forjado pelo cliente.
/// </summary>
public class HttpTenantProvider(IHttpContextAccessor accessor) : ITenantProvider
{
    public const string HeaderName = "X-Tenant-Id";

    public Guid TenantId
    {
        get
        {
            var value = accessor.HttpContext?.Request.Headers[HeaderName].FirstOrDefault();
            return Guid.TryParse(value, out var id)
                ? id
                : throw new TenantNotResolvedException();
        }
    }
}

public class TenantNotResolvedException()
    : Exception($"Header '{HttpTenantProvider.HeaderName}' ausente ou inválido (esperado um GUID).");
