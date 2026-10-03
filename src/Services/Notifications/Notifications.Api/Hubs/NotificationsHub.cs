using System.Security.Claims;
using HelpDeskFlow.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Notifications.Api.Domain;

namespace Notifications.Api.Hubs;

public record NotificationDto(Guid Id, string Subject, string Body, DateTime CreatedAt, DateTime? ReadAt)
{
    public static NotificationDto From(Notification n) => new(n.Id, n.Subject, n.Body, n.CreatedAt, n.ReadAt);
}

/// <summary>O que o SERVIDOR pode enviar ao navegador. O cliente não chama nada aqui: o hub é só de "empurrar".</summary>
public interface INotificationsClient
{
    Task NotificationReceived(NotificationDto notification);
    Task NotificationRead(Guid id);
}

/// <summary>
/// Entrega notificações em tempo real. Segurança:
///  • exige JWT válido (o token vem na query string, só nas rotas /hubs; veja HelpDeskFlow.Auth);
///  • o grupo de cada conexão é montado SÓ com as claims do token, nunca com algo enviado pelo cliente,
///    então ninguém consegue "entrar" no grupo de outra pessoa;
///  • não expõe nenhum método chamável pelo cliente;
///  • a conexão é encerrada quando o token expira (CloseOnAuthenticationExpiration, em Program.cs).
/// </summary>
[Authorize]
public class NotificationsHub : Hub<INotificationsClient>
{
    /// <summary>Nome do grupo de UMA pessoa em UMA empresa (todas as abas/dispositivos dela entram nele).</summary>
    public static string GroupFor(Guid tenantId, Guid userId) => $"user:{tenantId:N}:{userId:N}";

    public override async Task OnConnectedAsync()
    {
        var principal = Context.User;
        if (!Guid.TryParse(principal?.FindFirstValue(AppClaims.TenantId), out var tenantId)
            || !Guid.TryParse(principal.FindFirstValue(AppClaims.Subject), out var userId))
        {
            Context.Abort(); // token sem as claims esperadas: não deixa a conexão viver
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(tenantId, userId));
        await base.OnConnectedAsync();
    }
}
