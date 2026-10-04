using Notifications.Api.Messaging;

namespace HelpDeskFlow.UnitTests;

public class PriorityTextTests
{
    [Theory]
    [InlineData("Low", "baixa")]
    [InlineData("Medium", "média")]
    [InlineData("High", "alta")]
    [InlineData("Urgent", "urgente")]
    public void Traduz_a_prioridade_para_o_texto_que_a_pessoa_le(string prioridade, string esperado)
        => Assert.Equal(esperado, PriorityText.ToPortuguese(prioridade));

    [Fact]
    public void Valor_desconhecido_nao_some_nem_quebra()
        => Assert.Equal("critica", PriorityText.ToPortuguese("Critica"));
}