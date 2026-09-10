using ScorePlusTwo.Pipeline.Infraestructura;
using ScorePlusTwo.Pipeline.Modelos;
using ScorePlusTwo.Pipeline.Persistencia;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

// config/criterios-descartes.json existe para aislar el efecto de la etapa
// de rubro (ver Refiltrado/RefiltradoService y el rubro comodín de vocales):
// cualquier divergencia en tipos/estados/descarte_duro/exclusiones_rubro
// frente a config/criterios.json contamina esa medición con descartes que en
// producción ocurren en una etapa anterior a la que se quiere medir. Estos
// tests son la red de seguridad contra que alguien edite un archivo sin
// replicar el cambio en el otro. Rubros queda deliberadamente fuera de la
// comparación — es la única dimensión que este archivo existe para variar.
public class CriteriosDescartesTests
{
    private static readonly Criterios CriteriosReales = CargarCriterios("criterios.json");
    private static readonly Criterios CriteriosDescartes = CargarCriterios("criterios-descartes.json");

    private static Criterios CargarCriterios(string nombreArchivo)
    {
        var repoRoot = RutaRepo.Resolver();
        return JsonStore.Cargar<Criterios>(
            Path.Combine(repoRoot, "config", nombreArchivo), JsonOpciones.Config);
    }

    [Fact]
    public void DescarteDuroEsIdenticoAProduccion()
    {
        Assert.Equal(
            CriteriosReales.DescarteDuro.OrderBy(t => t, StringComparer.Ordinal),
            CriteriosDescartes.DescarteDuro.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void ExclusionesRubroSonIdenticasAProduccion()
    {
        Assert.Equal(
            CriteriosReales.ExclusionesRubro.OrderBy(t => t, StringComparer.Ordinal),
            CriteriosDescartes.ExclusionesRubro.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void TiposSonIdenticosAProduccion()
    {
        Assert.Equal(
            CriteriosReales.Tipos.OrderBy(t => t, StringComparer.Ordinal),
            CriteriosDescartes.Tipos.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void EstadosSonIdenticosAProduccion()
    {
        Assert.Equal(
            CriteriosReales.Estados.OrderBy(e => e),
            CriteriosDescartes.Estados.OrderBy(e => e));
    }

    [Fact]
    public void TiposPrivadosSonIdenticosAProduccion()
    {
        Assert.Equal(
            CriteriosReales.TiposPrivados.OrderBy(t => t, StringComparer.Ordinal),
            CriteriosDescartes.TiposPrivados.OrderBy(t => t, StringComparer.Ordinal));
    }
}
