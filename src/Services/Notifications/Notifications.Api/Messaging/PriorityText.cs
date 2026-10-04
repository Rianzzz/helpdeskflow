namespace Notifications.Api.Messaging;

/// <summary>
/// Os eventos trazem a prioridade pelo nome do enum em inglês ("Low", "Urgent"...), que é o contrato entre serviços.
/// O texto que a PESSOA lê na notificação sai em português, igual ao que a tela de chamados mostra.
/// </summary>
public static class PriorityText
{
    public static string ToPortuguese(string priority) => priority switch
    {
        "Low" => "baixa",
        "Medium" => "média",
        "High" => "alta",
        "Urgent" => "urgente",
        _ => priority.ToLowerInvariant(), // valor novo que este serviço ainda não conhece: melhor mostrar do que esconder
    };
}
