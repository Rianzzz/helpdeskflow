using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HelpDeskFlow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace HelpDeskFlow.IntegrationTests;

/// <summary>Comportamento entre serviços: eventos, idempotência, DLQ e jobs.</summary>
[Collection(StackCollection.Name)]
public class EventFlowTests(StackFixture stack)
{
    private async Task WaitForSubjectAsync(Person who, string startsWith) =>
        await Wait.UntilAsync(async () => (await stack.NotificationSubjectsAsync(who)).Any(s => s.StartsWith(startsWith)),
            $"notificação '{startsWith}' para {who.Role}");

    [Fact]
    public async Task New_ticket_notifies_staff_but_not_the_requester_or_other_companies()
    {
        var company = await stack.CreateCompanyAsync();
        var agent = await stack.AddUserAsync(company, "Agent");
        var customer = await stack.AddUserAsync(company, "Customer");
        var other = await stack.CreateCompanyAsync("Outra");
        var title = StackFixture.Unique("Impressora");

        await stack.CreateTicketAsync(customer, title);

        await WaitForSubjectAsync(company.Admin, $"Novo chamado: {title}");
        await WaitForSubjectAsync(agent, $"Novo chamado: {title}");

        // Deixa o sistema "assentar" e confirma as AUSÊNCIAS.
        await Task.Delay(1500);
        Assert.DoesNotContain(await stack.NotificationSubjectsAsync(customer), s => s.StartsWith("Novo chamado"));
        Assert.DoesNotContain(await stack.NotificationSubjectsAsync(other.Admin), s => s.Contains(title));
    }

    [Fact]
    public async Task The_very_first_ticket_right_after_signup_still_notifies_the_admin()
    {
        // Regressão: o TicketCreated podia chegar ao Notifications ANTES de ele conhecer o administrador (o
        // UserRegistered vem por outra fila). O aviso era descartado em silêncio. Repetimos algumas empresas para
        // dar chance à corrida de acontecer.
        for (var i = 0; i < 4; i++)
        {
            var company = await stack.CreateCompanyAsync($"Corrida{i}");
            var title = StackFixture.Unique("Primeiro");

            await stack.CreateTicketAsync(company.Admin, title); // imediatamente, sem esperar nada

            await WaitForSubjectAsync(company.Admin, $"Novo chamado: {title}");
        }
    }

    [Fact]
    public async Task Assigning_and_resolving_notify_the_right_people()
    {
        var company = await stack.CreateCompanyAsync();
        var agent = await stack.AddUserAsync(company, "Agent");
        var customer = await stack.AddUserAsync(company, "Customer");
        var title = StackFixture.Unique("Rede");
        var ticket = await stack.CreateTicketAsync(customer, title);

        await Wait.UntilAsync(async () =>
            (await stack.Tickets.SendAsync(agent.Token, HttpMethod.Put, $"/api/tickets/{ticket}/assign", new { assigneeId = agent.Id }))
            .StatusCode == HttpStatusCode.OK, "atribuição aceita");
        await stack.Tickets.SendAsync(agent.Token, HttpMethod.Put, $"/api/tickets/{ticket}/resolve");

        await WaitForSubjectAsync(agent, $"Chamado atribuído a você: {title}");
        await WaitForSubjectAsync(customer, $"Seu chamado está em atendimento: {title}");
        await WaitForSubjectAsync(customer, $"Chamado resolvido: {title}");
    }

    [Fact]
    public async Task Duplicate_delivery_of_the_same_event_creates_a_single_notification()
    {
        var company = await stack.CreateCompanyAsync();
        var customer = await stack.AddUserAsync(company, "Customer");
        // Espera o Notifications conhecer o cliente (cópia local alimentada pelo evento UserRegistered).
        await Wait.UntilAsync(async () =>
        {
            await using var scope = stack.NotificationsServices.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Notifications.Api.Data.NotificationsDbContext>();
            return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .AnyAsync(db.KnownUsers, u => u.Id == customer.Id);
        }, "Notifications conhecer o cliente");

        var publisher = stack.NotificationsServices.GetRequiredService<IEventPublisher>();
        var title = StackFixture.Unique("Duplicado");
        var evt = TicketResolved.Create(company.TenantId, Guid.NewGuid(), customer.Id, company.Admin.Id, title);

        await publisher.PublishAsync(evt);
        await publisher.PublishAsync(evt); // o MESMO evento (mesmo EventId), entregue duas vezes

        // Um evento "sentinela" posterior: filas são processadas em ordem, então, quando ele aparece,
        // as duas cópias anteriores já foram tratadas.
        var sentinel = StackFixture.Unique("Sentinela");
        await publisher.PublishAsync(TicketResolved.Create(company.TenantId, Guid.NewGuid(), customer.Id, company.Admin.Id, sentinel));
        await WaitForSubjectAsync(customer, $"Chamado resolvido: {sentinel}");

        var subjects = await stack.NotificationSubjectsAsync(customer);
        Assert.Single(subjects, s => s == $"Chamado resolvido: {title}");
    }

    [Fact]
    public async Task Poison_message_goes_to_the_dead_letter_queue()
    {
        const string dlq = "notifications.tickets.ticket-created.dlq";
        var factory = new ConnectionFactory { HostName = stack.RabbitHost, Port = stack.RabbitPort, UserName = "helpdesk", Password = "helpdesk_dev" };
        await using var conn = await factory.CreateConnectionAsync();
        await using var channel = await conn.CreateChannelAsync();
        var before = (await channel.QueueDeclarePassiveAsync(dlq)).MessageCount;

        await channel.BasicPublishAsync(Topology(), TicketCreated.EventName, mandatory: false,
            new BasicProperties { Persistent = true }, Encoding.UTF8.GetBytes("isto não é JSON {{{"));

        await Wait.UntilAsync(async () => (await channel.QueueDeclarePassiveAsync(dlq)).MessageCount > before,
            "mensagem inválida chegar à DLQ");
    }

    private static string Topology() => HelpDeskFlow.Messaging.RabbitMq.Topology.EventsExchange;

    [Fact]
    public async Task Unattended_urgent_ticket_breaches_the_SLA_and_escalates_to_admins_only()
    {
        var company = await stack.CreateCompanyAsync();
        var agent = await stack.AddUserAsync(company, "Agent");
        var customer = await stack.AddUserAsync(company, "Customer");
        var title = StackFixture.Unique("Urgente");

        var ticket = await stack.CreateTicketAsync(customer, title, "Urgent");

        // O job (SLA de ~2 s nos testes) marca o chamado...
        await Wait.UntilAsync(async () =>
        {
            var body = await (await stack.Tickets.SendAsync(customer.Token, HttpMethod.Get, $"/api/tickets/{ticket}"))
                .Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("slaBreachedAt").ValueKind != JsonValueKind.Null;
        }, "SLA ser marcado como estourado");

        // ...e o evento escala para o Admin.
        await WaitForSubjectAsync(company.Admin, $"SLA estourado: {title}");
        Assert.DoesNotContain(await stack.NotificationSubjectsAsync(agent), s => s.StartsWith("SLA estourado"));
    }

    [Fact]
    public async Task Users_only_see_and_can_only_mark_their_own_notifications()
    {
        var company = await stack.CreateCompanyAsync();
        var agent = await stack.AddUserAsync(company, "Agent");
        await stack.CreateTicketAsync(company.Admin, StackFixture.Unique("ntf"));
        await WaitForSubjectAsync(company.Admin, "Novo chamado");

        var list = await (await stack.Notifications.SendAsync(company.Admin.Token, HttpMethod.Get, "/api/notifications"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = list[0].GetProperty("id").GetGuid();

        var byOther = await stack.Notifications.SendAsync(agent.Token, HttpMethod.Put, $"/api/notifications/{id}/read");
        var byOwner = await stack.Notifications.SendAsync(company.Admin.Token, HttpMethod.Put, $"/api/notifications/{id}/read");

        Assert.Equal(HttpStatusCode.NotFound, byOther.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, byOwner.StatusCode);
    }
}
