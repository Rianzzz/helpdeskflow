using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace HelpDeskFlow.IntegrationTests;

/// <summary>Notificações em tempo real (SignalR): um cliente de verdade, conectado por WebSocket ao serviço Notifications.</summary>
[Collection(StackCollection.Name)]
public class RealtimeTests(StackFixture stack) : IAsyncLifetime
{
    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
    }

    /// <summary>Conecta como "essa pessoa" e devolve a conexão + o que ela for recebendo.</summary>
    private async Task<Listener> ConnectAsync(string? token)
    {
        var server = stack.NotificationsServer;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/notifications"), o =>
            {
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                // Imita o NAVEGADOR: WebSocket de navegador não envia cabeçalho Authorization, então o token vai na
                // query string ("?access_token="). É exatamente o caminho que o front-end usa em produção.
                o.WebSocketFactory = (context, ct) =>
                {
                    var uri = token is null
                        ? context.Uri
                        : new Uri($"{context.Uri}{(string.IsNullOrEmpty(context.Uri.Query) ? "?" : "&")}access_token={Uri.EscapeDataString(token)}");
                    return new ValueTask<System.Net.WebSockets.WebSocket>(server.CreateWebSocketClient().ConnectAsync(uri, ct));
                };
                o.Transports = HttpTransportType.WebSockets;
                if (token is not null) o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        _connections.Add(connection);

        var listener = new Listener(connection);
        await connection.StartAsync();
        return listener;
    }

    private sealed class Listener
    {
        public ConcurrentQueue<JsonElement> Received { get; } = new();
        public ConcurrentQueue<Guid> Read { get; } = new();
        public HubConnection Connection { get; }

        public Listener(HubConnection connection)
        {
            Connection = connection;
            connection.On<JsonElement>("NotificationReceived", n => Received.Enqueue(n));
            connection.On<Guid>("NotificationRead", id => Read.Enqueue(id));
        }

        public IEnumerable<string> Subjects => Received.Select(n => n.GetProperty("subject").GetString()!);
    }

    [Fact]
    public async Task Users_receive_their_own_notifications_instantly_and_nobody_else_does()
    {
        var company = await stack.CreateCompanyAsync();
        var agent = await stack.AddUserAsync(company, "Agent");
        var customer = await stack.AddUserAsync(company, "Customer");
        var other = await stack.CreateCompanyAsync("Outra");
        var title = StackFixture.Unique("Tempo real");

        var admin = await ConnectAsync(company.Admin.Token);
        var agentHub = await ConnectAsync(agent.Token);
        var customerHub = await ConnectAsync(customer.Token);
        var otherHub = await ConnectAsync(other.Admin.Token);

        await stack.CreateTicketAsync(customer, title);

        // O aviso chega por empurrão (sem ninguém consultar a API).
        await Wait.UntilAsync(() => Task.FromResult(admin.Subjects.Contains($"Novo chamado: {title}")), "admin receber o aviso em tempo real");
        await Wait.UntilAsync(() => Task.FromResult(agentHub.Subjects.Contains($"Novo chamado: {title}")), "agente receber o aviso em tempo real");

        // Deixa o sistema assentar e confirma as AUSÊNCIAS: o cliente (não é equipe) e a outra empresa não recebem nada.
        await Task.Delay(1500);
        Assert.DoesNotContain(customerHub.Subjects, s => s.Contains(title));
        Assert.DoesNotContain(otherHub.Subjects, s => s.Contains(title));
    }

    [Fact]
    public async Task The_pushed_payload_matches_what_the_REST_API_returns()
    {
        var company = await stack.CreateCompanyAsync();
        var admin = await ConnectAsync(company.Admin.Token);
        var title = StackFixture.Unique("Payload");

        await stack.CreateTicketAsync(company.Admin, title);
        await Wait.UntilAsync(() => Task.FromResult(admin.Subjects.Contains($"Novo chamado: {title}")), "aviso empurrado");

        var pushed = admin.Received.First(n => n.GetProperty("subject").GetString() == $"Novo chamado: {title}");
        var rest = (await (await stack.Notifications.SendAsync(company.Admin.Token, HttpMethod.Get, "/api/notifications"))
            .Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .First(n => n.GetProperty("id").GetGuid() == pushed.GetProperty("id").GetGuid());

        Assert.Equal(rest.GetProperty("body").GetString(), pushed.GetProperty("body").GetString());
        Assert.Equal(JsonValueKind.Null, pushed.GetProperty("readAt").ValueKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("isto.nao.e-um-jwt")]
    public async Task The_hub_refuses_connections_without_a_valid_token(string? token)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(token));

        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task A_token_in_the_query_string_is_accepted_ONLY_on_hub_routes()
    {
        var company = await stack.CreateCompanyAsync();
        var token = company.Admin.Token;

        // Na rota do hub (negociação do SignalR), o token na URL é aceito...
        var negotiate = await stack.Notifications.PostAsync($"/hubs/notifications/negotiate?negotiateVersion=1&access_token={token}", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);

        // ...mas em QUALQUER outra rota é ignorado: um token na URL vazaria em logs, histórico e Referer.
        foreach (var (client, url) in new[]
                 {
                     (stack.Notifications, $"/api/notifications?access_token={token}"),
                     (stack.Tickets, $"/api/tickets?access_token={token}"),
                     (stack.Identity, $"/api/users/me?access_token={token}"),
                 })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Marking_as_read_updates_every_open_session_of_the_same_user_but_not_other_users()
    {
        var company = await stack.CreateCompanyAsync();
        var agent = await stack.AddUserAsync(company, "Agent");
        var tabA = await ConnectAsync(company.Admin.Token); // duas abas/dispositivos da MESMA pessoa
        var tabB = await ConnectAsync(company.Admin.Token);
        var agentHub = await ConnectAsync(agent.Token);
        var title = StackFixture.Unique("Leitura");

        await stack.CreateTicketAsync(company.Admin, title);
        await Wait.UntilAsync(() => Task.FromResult(tabA.Subjects.Contains($"Novo chamado: {title}")), "aviso na aba A");
        var id = tabA.Received.First(n => n.GetProperty("subject").GetString() == $"Novo chamado: {title}").GetProperty("id").GetGuid();

        var response = await stack.Notifications.SendAsync(company.Admin.Token, HttpMethod.Put, $"/api/notifications/{id}/read");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await Wait.UntilAsync(() => Task.FromResult(tabA.Read.Contains(id) && tabB.Read.Contains(id)), "as duas abas saberem que foi lida");
        await Task.Delay(1000);
        Assert.Empty(agentHub.Read); // o agente não recebe eventos de leitura de outra pessoa
    }

    [Fact]
    public async Task The_hub_exposes_no_client_callable_methods()
    {
        var company = await stack.CreateCompanyAsync();
        var hub = (await ConnectAsync(company.Admin.Token)).Connection;

        // O cliente não consegue se inscrever em grupos nem pedir dados: só o servidor empurra.
        foreach (var method in new[] { "AddToGroup", "JoinGroup", "Subscribe", "SendMessage", "GetNotifications" })
            await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync(method, "user:qualquer:grupo"));
    }
}
