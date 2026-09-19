using ScorePlusTwo.Pipeline.Filtro;
using ScorePlusTwo.Pipeline.Modelos;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

public class AplicadorOverridesTests
{
    private static Candidata Candidata(string codigo) => new()
    {
        Codigo = codigo,
        Nombre = "Prueba " + codigo,
        Tipo = "LE",
    };

    [Fact]
    public void SinOverrides_NoCambiaNada()
    {
        var prioritarias = new List<Candidata> { Candidata("1-1-LE26") };
        var secundarias = new List<Candidata> { Candidata("2-2-LE26") };

        var (resultPrioritarias, resultSecundarias, cambio) = AplicadorOverrides.Aplicar(
            new Dictionary<string, EntradaOverride>(), prioritarias, secundarias);

        Assert.False(cambio);
        Assert.Equal(new[] { "1-1-LE26" }, resultPrioritarias.Select(c => c.Codigo));
        Assert.Equal(new[] { "2-2-LE26" }, resultSecundarias.Select(c => c.Codigo));
    }

    [Fact]
    public void OverrideMueveDeSecundariasAPrioritarias()
    {
        var prioritarias = new List<Candidata>();
        var secundarias = new List<Candidata> { Candidata("85-41-LE26") };
        var overrides = new Dictionary<string, EntradaOverride>
        {
            ["85-41-LE26"] = new EntradaOverride(ListaDestino.Prioritarias, true, DateTime.UtcNow),
        };

        var (resultPrioritarias, resultSecundarias, cambio) = AplicadorOverrides.Aplicar(
            overrides, prioritarias, secundarias);

        Assert.True(cambio);
        Assert.Equal(new[] { "85-41-LE26" }, resultPrioritarias.Select(c => c.Codigo));
        Assert.Empty(resultSecundarias);
    }

    [Fact]
    public void OverrideMueveDePrioritariasASecundarias()
    {
        var prioritarias = new List<Candidata> { Candidata("2345-148-LE26") };
        var secundarias = new List<Candidata>();
        var overrides = new Dictionary<string, EntradaOverride>
        {
            ["2345-148-LE26"] = new EntradaOverride(ListaDestino.Secundarias, true, DateTime.UtcNow),
        };

        var (resultPrioritarias, resultSecundarias, cambio) = AplicadorOverrides.Aplicar(
            overrides, prioritarias, secundarias);

        Assert.True(cambio);
        Assert.Empty(resultPrioritarias);
        Assert.Equal(new[] { "2345-148-LE26" }, resultSecundarias.Select(c => c.Codigo));
    }

    [Fact]
    public void OverrideYaAplicado_EsNoOpIdempotente()
    {
        // Un override "confirmar en secundarias" para un código que ya está
        // en Secundarias no debe moverlo ni reportarse como cambio — esto es
        // lo que hace que reaplicar el mismo override día tras día (nunca
        // expira) sea seguro.
        var prioritarias = new List<Candidata>();
        var secundarias = new List<Candidata> { Candidata("598-16-LE26") };
        var overrides = new Dictionary<string, EntradaOverride>
        {
            ["598-16-LE26"] = new EntradaOverride(ListaDestino.Secundarias, true, DateTime.UtcNow),
        };

        var (resultPrioritarias, resultSecundarias, cambio) = AplicadorOverrides.Aplicar(
            overrides, prioritarias, secundarias);

        Assert.False(cambio);
        Assert.Empty(resultPrioritarias);
        Assert.Equal(new[] { "598-16-LE26" }, resultSecundarias.Select(c => c.Codigo));
    }

    [Fact]
    public void OverrideDeCodigoInexistente_NoHaceNada()
    {
        var prioritarias = new List<Candidata> { Candidata("1-1-LE26") };
        var secundarias = new List<Candidata> { Candidata("2-2-LE26") };
        var overrides = new Dictionary<string, EntradaOverride>
        {
            ["9-9-LE26"] = new EntradaOverride(ListaDestino.Prioritarias, true, DateTime.UtcNow),
        };

        var (resultPrioritarias, resultSecundarias, cambio) = AplicadorOverrides.Aplicar(
            overrides, prioritarias, secundarias);

        Assert.False(cambio);
        Assert.Equal(new[] { "1-1-LE26" }, resultPrioritarias.Select(c => c.Codigo));
        Assert.Equal(new[] { "2-2-LE26" }, resultSecundarias.Select(c => c.Codigo));
    }
}
