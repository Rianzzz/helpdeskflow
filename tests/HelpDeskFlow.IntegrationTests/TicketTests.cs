using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HelpDeskFlow.IntegrationTests;

[Collection(StackCollection.Name)]
public class TicketTests(StackFixture stack)
{
    private async Task<JsonElement> ListAsync(Person who) =>
        await (await stack.Tickets.SendAsync(who.Token, HttpMethod.Get, "/api/tickets")).Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>
    /// A atribuição depende da cópia local de usuários do Tickets, alimentada por eventos do Identity (consistência
    /// eventual). Repetimos até o responsável ser reconhecido.
    /// </summary>
    private async Task AssignWhenKnownAsync(Person actor, Guid ticketId, Guid assigneeId)
    {
        await Wait.UntilAsync(async () =>
        {
            var response = await stack.Tickets.SendAsync(actor.Token, HttpMethod.Put, $"/api/tickets/{ticketId}/assign", new { assigneeId });
            return response.StatusCode == HttpStatusCode.OK;
        }, "responsável ser reconhecido pelo Tickets");
    }

    [Fact]
    public async Task Companies_cannot_see_each_others_tickets()
    {
        var a = await stack.CreateCompanyAsync("A");
        var b = await stack.CreateCompanyAsync("B");
        var ticketId = await stack.CreateTicketAsync(a.Admin, "Segredo da empresa A");

        var listB = await ListAsync(b.Admin);
        var getB = await stack.Tickets.SendAsync(b.Admin.Token, HttpMethod.Get, $"/api/tickets/{ticketId}");
        var resolveB = await stack.Tickets.SendAsync(b.Admin.Token, HttpMethod.Put, $"/api/tickets/{ticketId}/resolve");

        Assert.Empty(listB.EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, getB.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, resolveB.StatusCode);
    }

    [Fact]
    public async Task Customers_only_see_their_own_tickets_while_staff_see_the_whole_company()
    {
        var company = await stack.CreateCompanyAsync();
        var agent = await stack.AddUserAsync(company, "Agent");
        var alice = await stack.AddUserAsync(company, "Customer");
        var bob = await stack.AddUserAsync(company, "Customer");
        await stack.CreateTicketAsync(alice, "da Alice");
        var bobsTicket = await stack.CreateTicketAsync(bob, "do Bob");

        var aliceView = await ListAsync(alice);
        var agentView = await ListAsync(agent);
        var aliceReadsBob = await stack.Tickets.SendAsync(alice.Token, HttpMethod.Get, $"/api/tickets/{bobsTicket}");

        Assert.Single(aliceView.EnumerateArray());
        Assert.Equal("da Alice", aliceView[0].GetProperty("title").GetString());
        Assert.Equal(2, agentView.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, aliceReadsBob.StatusCode);
    }

    [Fact]
    public async Task Customers_cannot_assign_resolve_or_close()
    {
        var company = await stack.CreateCompanyAsync();
        var customer = await stack.AddUserAsync(company, "Customer");
        var id = await stack.CreateTicketAsync(customer, "meu chamado");

        foreach (var action in new[] { "resolve", "close" })
        {
            var response = await stack.Tickets.SendAsync(customer.Token, HttpMethod.Put, $"/api/tickets/{id}/{action}");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var assign = await stack.Tickets.SendAsync(customer.Token, HttpMethod.Put, $"/api/tickets/{id}/assign", new { assigneeId = customer.Id });
        Assert.Equal(HttpStatusCode.Forbidden, assign.StatusCode);
    }

    [Fact]
    public async Task Ticket_lifecycle_open_assign_resolve_close_reopen()
    {
        var company = await stack.CreateCompanyAsync();
        var agent = await stack.AddUserAsync(company, "Agent");
        var customer = await stack.AddUserAsync(company, "Customer");
        var id = await stack.CreateTicketAsync(customer, "ciclo completo");

        await AssignWhenKnownAsync(agent, id, agent.Id);
        var resolved = await stack.Tickets.SendAsync(agent.Token, HttpMethod.Put, $"/api/tickets/{id}/resolve");
        var closed = await stack.Tickets.SendAsync(agent.Token, HttpMethod.Put, $"/api/tickets/{id}/close");
        var reopened = await stack.Tickets.SendAsync(customer.Token, HttpMethod.Put, $"/api/tickets/{id}/reopen"); // o cliente pode reabrir o próprio

        Assert.Equal("Resolved", (await resolved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal("Closed", (await closed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal("Open", (await reopened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Closing_before_resolving_is_a_business_rule_violation()
    {
        var company = await stack.CreateCompanyAsync();
        var id = await stack.CreateTicketAsync(company.Admin, "x");

        var response = await stack.Tickets.SendAsync(company.Admin.Token, HttpMethod.Put, $"/api/tickets/{id}/close");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("resolvido", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Assignee_is_validated_against_users_replicated_from_Identity()
    {
        var a = await stack.CreateCompanyAsync("A");
        var b = await stack.CreateCompanyAsync("B");
        var agentA = await stack.AddUserAsync(a, "Agent");
        var agentB = await stack.AddUserAsync(b, "Agent");
        var customerA = await stack.AddUserAsync(a, "Customer");
        var ticket = await stack.CreateTicketAsync(a.Admin, "para atribuir");

        // Um responsável válido prova que a replicação de usuários do tenant A já aconteceu.
        await AssignWhenKnownAsync(a.Admin, ticket, agentA.Id);

        async Task<HttpStatusCode> AssignAsync(Guid assignee) =>
            (await stack.Tickets.SendAsync(a.Admin.Token, HttpMethod.Put, $"/api/tickets/{ticket}/assign", new { assigneeId = assignee })).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await AssignAsync(Guid.NewGuid()));   // não existe
        Assert.Equal(HttpStatusCode.BadRequest, await AssignAsync(customerA.Id));      // existe, mas não é da equipe
        Assert.Equal(HttpStatusCode.BadRequest, await AssignAsync(agentB.Id));         // é equipe, mas de OUTRA empresa
    }

    [Fact]
    public async Task Invalid_input_is_a_400_not_a_500()
    {
        var company = await stack.CreateCompanyAsync();

        var emptyTitle = await stack.Tickets.SendAsync(company.Admin.Token, HttpMethod.Post, "/api/tickets",
            new { title = "  ", description = "", priority = "Low" });

        Assert.Equal(HttpStatusCode.BadRequest, emptyTitle.StatusCode);
    }
}
