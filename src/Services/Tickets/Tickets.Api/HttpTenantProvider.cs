using System.Security.Claims;
using HelpDeskFlow.Auth;
using Tickets.Application.Abstractions;

namespace Tickets.Api;

/// <summary>
/// Lê quem é o usuário a partir das claims do JWT. O token já foi validado (assinatura, expiração,
/// emissor e audiência) pelo middleware de autenticação, então estas claims são confiáveis:
/// o cliente não consegue forjá-las sem a chave de assinatura do Identity.
/// </summary>
public class ClaimsCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal Principal =>
        accessor.HttpContext?.User is { Identity.IsAuthenticated: true } p ? p : throw new InvalidTokenClaimsException();

    public Guid TenantId => ReadGuid(AppClaims.TenantId);
    public Guid UserId => ReadGuid(AppClaims.Subject);
    public bool IsCustomer => Principal.IsInRole(Roles.Customer);

    private Guid ReadGuid(string claim) =>
        Guid.TryParse(Principal.FindFirstValue(claim), out var id) ? id : throw new InvalidTokenClaimsException();
}

public class InvalidTokenClaimsException() : Exception("Token sem as claims necessárias.");
