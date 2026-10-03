using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace HelpDeskFlow.IntegrationTests;

/// <summary>Conversa nos chamados: respostas públicas, notas internas (só equipe) e os avisos de cada caso.</summary>
[Collection(StackCollection.Name)]
public class CommentTests(StackFixture stack)
{
    private Task<HttpResponseMessage> PostAsync(Person who, Guid ticket, string body, bool isInternal = false) =>
        stack.Tickets.SendAsync(who.Token, HttpMethod.Post, $"/api/tickets/{ticket}/comments", new { body, isInternal });

    private async Task<List<JsonElement>> ListAsync(Person who, Guid ticket)
    {
        var response = await stack.Tickets.SendAsync(who.Token, HttpMethod.Get, $"/api/tickets/{ticket}/comments");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    private async Task<(Company Company, Person Agent, Person Customer, Guid Ticket)> ScenarioAsync(string title)
    {
        var company = await stack.CreateCompanyAsync();
        var agent = await stack.AddUserAsync(company, "Agent");
        var customer = await stack.AddUserAsync(company, "Customer");
        var ticket = await stack.CreateTicketAsync(customer, title);
        return (company, agent, customer, ticket);
    }

    private async Task WaitForSubjectAsync(Person who, string subject) =>
        await Wait.UntilAsync(async () => (await stack.NotificationSubjectsAsync(who)).Contains(subject), $"{who.Role} receber '{subject}'");

    /// <summary>Espera o Notifications conhecer a equipe (replicação por evento) antes de afirmar AUSÊNCIA de avisos.</summary>
    private async Task WaitUntilTenantIsKnownAsync(Company company, Person agent) =>
        await Wait.UntilAsync(async () =>
        {
            await using var scope = stack.NotificationsServices.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Notifications.Api.Data.NotificationsDbContext>();
            return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .CountAsync(db.KnownUsers, u => u.Id == agent.Id || u.Id == company.Admin.Id) == 2;
        }, "Notifications conhecer a equipe");

    // ───────────── API: quem vê e quem escreve ─────────────

    [Fact]
    public async Task Customer_and_staff_converse_and_each_sees_the_right_thread()
    {
        var (_, agent, customer, ticket) = await ScenarioAsync(StackFixture.Unique("Conversa"));

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(agent, ticket, "Pode reiniciar o equipamento?")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(agent, ticket, "Cliente parece estar com pressa.", isInternal: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(customer, ticket, "Reiniciei e voltou a funcionar.")).StatusCode);

        // O nome e o papel do autor vêm da cópia local de usuários (replicada por evento): esperamos ela existir.
        await Wait.UntilAsync(async () => (await ListAsync(agent, ticket))[0].GetProperty("authorRole").ValueKind == JsonValueKind.String,
            "Tickets conhecer o autor do comentário");

        var asCustomer = await ListAsync(customer, ticket);
        var asAgent = await ListAsync(agent, ticket);

        Assert.Equal(["Pode reiniciar o equipamento?", "Reiniciei e voltou a funcionar."], asCustomer.Select(c => c.GetProperty("body").GetString()));
        Assert.Equal(3, asAgent.Count); // a equipe vê também a nota interna
        Assert.All(asCustomer, c => Assert.False(c.GetProperty("isInternal").GetBoolean())); // nada interno vaza para o cliente
        Assert.Equal("Agent", asAgent[0].GetProperty("authorRole").GetString());
        Assert.Equal(agent.Id, asAgent[0].GetProperty("authorId").GetGuid());
    }

    [Fact]
    public async Task Internal_notes_never_leak_into_the_customers_raw_response()
    {
        var (_, agent, customer, ticket) = await ScenarioAsync(StackFixture.Unique("Vazamento"));
        await PostAsync(agent, ticket, "SEGREDO-INTERNO-12345", isInternal: true);

        var raw = await (await stack.Tickets.SendAsync(customer.Token, HttpMethod.Get, $"/api/tickets/{ticket}/comments"))
            .Content.ReadAsStringAsync();

        Assert.DoesNotContain("SEGREDO-INTERNO-12345", raw);
    }

    [Fact]
    public async Task Customers_cannot_create_internal_notes_over_the_API()
    {
        var (_, _, customer, ticket) = await ScenarioAsync(StackFixture.Unique("Proibido"));

        var response = await PostAsync(customer, ticket, "nota interna do cliente", isInternal: true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("equipe", await response.Content.ReadAsStringAsync());
        Assert.Empty(await ListAsync(customer, ticket)); // e nada foi gravado
    }

    [Fact]
    public async Task Another_customer_cannot_read_or_comment_on_someone_elses_ticket()
    {
        var (company, _, owner, ticket) = await ScenarioAsync(StackFixture.Unique("Alheio"));
        var intruder = await stack.AddUserAsync(company, "Customer");

        var read = await stack.Tickets.SendAsync(intruder.Token, HttpMethod.Get, $"/api/tickets/{ticket}/comments");
        var write = await PostAsync(intruder, ticket, "oi, sou intruso");

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        Assert.Empty(await ListAsync(owner, ticket));
    }

    [Fact]
    public async Task Another_company_cannot_read_or_comment_even_as_admin()
    {
        var (_, _, _, ticket) = await ScenarioAsync(StackFixture.Unique("Outra empresa"));
        var other = await stack.CreateCompanyAsync("Estranha");

        var read = await stack.Tickets.SendAsync(other.Admin.Token, HttpMethod.Get, $"/api/tickets/{ticket}/comments");
        var write = await PostAsync(other.Admin, ticket, "invasão");

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
    }

    [Fact]
    public async Task Closed_tickets_reject_comments_until_reopened()
    {
        var (_, agent, customer, ticket) = await ScenarioAsync(StackFixture.Unique("Fechado"));
        await stack.Tickets.SendAsync(agent.Token, HttpMethod.Put, $"/api/tickets/{ticket}/resolve");
        await stack.Tickets.SendAsync(agent.Token, HttpMethod.Put, $"/api/tickets/{ticket}/close");

        var rejected = await PostAsync(customer, ticket, "ainda preciso de ajuda");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("Reabra", await rejected.Content.ReadAsStringAsync());

        await stack.Tickets.SendAsync(customer.Token, HttpMethod.Put, $"/api/tickets/{ticket}/reopen");
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(customer, ticket, "agora vai")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    public async Task Empty_comments_are_a_400(string body)
    {
        var (_, agent, _, ticket) = await ScenarioAsync(StackFixture.Unique("Vazio"));

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(agent, ticket, body)).StatusCode);
    }

    [Fact]
    public async Task Overlong_comments_are_a_400_not_a_500()
    {
        var (_, agent, _, ticket) = await ScenarioAsync(StackFixture.Unique("Longo"));

        var response = await PostAsync(agent, ticket, new string('a', 4001));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        var (_, _, _, ticket) = await ScenarioAsync(StackFixture.Unique("Anonimo"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await stack.Tickets.GetAsync($"/api/tickets/{ticket}/comments")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await stack.Tickets.PostAsJsonAsync($"/api/tickets/{ticket}/comments", new { body = "oi" })).StatusCode);
    }

    // ───────────── Avisos: quem é avisado de cada comentário ─────────────

    [Fact]
    public async Task A_public_reply_from_staff_notifies_the_requester_and_not_the_author()
    {
        var title = StackFixture.Unique("Resposta");
        var (_, agent, customer, ticket) = await ScenarioAsync(title);

        await PostAsync(agent, ticket, "Estamos analisando.");

        await WaitForSubjectAsync(customer, $"Nova resposta em: {title}");
        await Task.Delay(1500);
        Assert.DoesNotContain(await stack.NotificationSubjectsAsync(agent), s => s == $"Nova resposta em: {title}"); // o autor não é avisado
    }

    [Fact]
    public async Task A_comment_from_the_requester_notifies_the_staff()
    {
        var title = StackFixture.Unique("Cliente escreveu");
        var (company, agent, customer, ticket) = await ScenarioAsync(title);
        await WaitUntilTenantIsKnownAsync(company, agent); // a equipe é escolhida pelo que o Notifications conhece NO MOMENTO

        await PostAsync(customer, ticket, "Alguma novidade?");

        await WaitForSubjectAsync(agent, $"Nova resposta em: {title}");
        await WaitForSubjectAsync(company.Admin, $"Nova resposta em: {title}"); // sem responsável: toda a equipe
    }

    [Fact]
    public async Task An_internal_note_notifies_staff_but_NEVER_the_requester()
    {
        var title = StackFixture.Unique("Nota interna");
        var (company, agent, customer, ticket) = await ScenarioAsync(title);
        await WaitUntilTenantIsKnownAsync(company, agent);

        await PostAsync(agent, ticket, "Conversar com o fornecedor antes.", isInternal: true);

        await WaitForSubjectAsync(company.Admin, $"Nota interna em: {title}"); // a equipe é avisada...

        // ...e o cliente NÃO. Um comentário público posterior em outro chamado serve de "sentinela": quando o cliente
        // recebe o aviso dele, tudo o que veio antes na fila já foi tratado.
        var sentinelTitle = StackFixture.Unique("Sentinela");
        var sentinel = await stack.CreateTicketAsync(customer, sentinelTitle);
        await PostAsync(agent, sentinel, "Resposta pública.");
        await WaitForSubjectAsync(customer, $"Nova resposta em: {sentinelTitle}");

        var subjects = await stack.NotificationSubjectsAsync(customer);
        Assert.DoesNotContain(subjects, s => s.Contains("interna", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(subjects, s => s.Contains(title) && !s.StartsWith("Chamado")); // nada sobre esse chamado, exceto os dele
    }

    [Fact]
    public async Task Notifications_for_comments_are_delivered_in_real_time()
    {
        var title = StackFixture.Unique("Ao vivo");
        var (_, agent, customer, ticket) = await ScenarioAsync(title);
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();

        var server = stack.NotificationsServer;
        var connection = new Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/notifications"), o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(customer.Token); // autentica a negociação HTTP
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                o.WebSocketFactory = (ctx, ct) => new ValueTask<System.Net.WebSockets.WebSocket>(
                    server.CreateWebSocketClient().ConnectAsync(new Uri($"{ctx.Uri}&access_token={Uri.EscapeDataString(customer.Token)}"), ct));
            })
            .Build();
        connection.On<JsonElement>("NotificationReceived", n => received.Enqueue(n.GetProperty("subject").GetString()!));
        await connection.StartAsync();

        await PostAsync(agent, ticket, "Resposta do atendimento.");

        await Wait.UntilAsync(() => Task.FromResult(received.Contains($"Nova resposta em: {title}")), "aviso do comentário chegar por empurrão");
        await connection.DisposeAsync();
    }
}
