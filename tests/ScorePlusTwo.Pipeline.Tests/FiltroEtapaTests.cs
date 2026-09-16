using ScorePlusTwo.Pipeline.Filtro;
using ScorePlusTwo.Pipeline.Modelos;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

public class FiltroEtapaTests
{
    private static Criterios CriteriosDePrueba() => new(
        Version: "test",
        Tipos: new List<string> { "LE", "L1", "CO" },
        Estados: new List<int> { 5 },
        Regiones: new List<string>(),
        Rubros: new List<RubroCriterio>
        {
            new("compliance", "alta", new List<string> { "auditor" }),
            new("vigilancia", "secundaria", new List<string> { "camara" }),
        },
        DescarteDuro: new List<string> { "vehiculo", "construccion" },
        TiposPrivados: new List<string> { "CO" });

    private static LicitacionRaw Licitacion(string codigo, string nombre, int estado) =>
        new(codigo, nombre, estado, new DateTime(2026, 9, 10));

    [Fact]
    public void EstadoDesconocido_NoCrashea_YQuedaFueraDelFiltro()
    {
        var licitaciones = new[] { Licitacion("1-1-LE26", "AUDITORIA GENERAL", 999) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Empty(resultado.Prioritarias);
        Assert.Equal(0, resultado.TrasEstado);
    }

    [Fact]
    public void TiposDescartados_NoPasan()
    {
        var licitaciones = new[]
        {
            Licitacion("1-1-L226", "AUDITORIA GENERAL", 5),
            Licitacion("1-1-B226", "AUDITORIA GENERAL", 5),
            Licitacion("1-1-O126", "AUDITORIA GENERAL", 5),
        };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Equal(3, resultado.TrasEstado);
        Assert.Equal(0, resultado.TrasTipo);
        Assert.Empty(resultado.Prioritarias);
    }

    [Fact]
    public void CodigoMalformado_NoCrashea_YNoMatchea()
    {
        var licitaciones = new[] { Licitacion("SINGUION", "AUDITORIA GENERAL", 5) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Equal(0, resultado.TrasTipo);
        Assert.Empty(resultado.Prioritarias);
    }

    [Fact]
    public void DescarteDuroGanaSiempre_AunqueTambienMatcheeRubro()
    {
        var licitaciones = new[] { Licitacion("1-1-LE26", "AUDITORIA DE VEHICULOS MENORES", 5) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Equal(1, resultado.DescarteDuro);
        Assert.Empty(resultado.Prioritarias);
        Assert.Empty(resultado.Secundarias);
        Assert.Empty(resultado.TramoBajo);
    }

    [Fact]
    public void ObraPublica_NoApareceEnNingunaDeLasTresListas()
    {
        // Descarte real: obras públicas y suministros no se clasifican, se
        // destruyen — es el único conjunto que debe desaparecer sin rastro.
        var licitaciones = new[] { Licitacion("1-1-LE26", "CONSTRUCCION DE PUENTE PEATONAL", 5) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Equal(1, resultado.DescarteDuro);
        Assert.DoesNotContain(resultado.Prioritarias, c => c.Origen.CodigoExterno == "1-1-LE26");
        Assert.DoesNotContain(resultado.Secundarias, c => c.Origen.CodigoExterno == "1-1-LE26");
        Assert.DoesNotContain(resultado.TramoBajo, c => c.Origen.CodigoExterno == "1-1-LE26");
    }

    [Fact]
    public void MatchPorSubstring_NoPalabraCompleta()
    {
        var licitaciones = new[] { Licitacion("1-1-LE26", "SERVICIO DE AUDITORIAS EXTERNAS", 5) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        var candidata = Assert.Single(resultado.Prioritarias);
        Assert.Equal("compliance", candidata.RubroMatch);
        Assert.Equal("auditor", candidata.TerminoMatch);
    }

    [Fact]
    public void RubroSecundario_VaASecundarias_NoAPrioritarias()
    {
        var licitaciones = new[] { Licitacion("1-1-LE26", "INSTALACION DE CAMARA DE SEGURIDAD", 5) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Empty(resultado.Prioritarias);
        var secundaria = Assert.Single(resultado.Secundarias);
        Assert.Equal("vigilancia", secundaria.RubroMatch);
        Assert.Equal("camara", secundaria.TerminoMatch);
    }

    [Fact]
    public void SinNingunRubro_VaASecundarias_ComoInventarioDeProspeccion()
    {
        var licitaciones = new[] { Licitacion("1-1-LE26", "SERVICIO GENERICO SIN RUBRO CONOCIDO", 5) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Empty(resultado.Prioritarias);
        var secundaria = Assert.Single(resultado.Secundarias);
        Assert.Null(secundaria.RubroMatch);
        Assert.Null(secundaria.TerminoMatch);
    }

    [Fact]
    public void TipoL1_VaATramoBajo_NoSeMezclaConElResto()
    {
        var licitaciones = new[] { Licitacion("1-1-L126", "AUDITORIA GENERAL", 5) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Empty(resultado.Prioritarias);
        Assert.Empty(resultado.Secundarias);
        var tramoBajo = Assert.Single(resultado.TramoBajo);
        Assert.Null(tramoBajo.RubroMatch);
        Assert.Null(tramoBajo.TerminoMatch);
    }

    [Fact]
    public void TipoL1_TambienSujetoADescarteDuro()
    {
        var licitaciones = new[] { Licitacion("1-1-L126", "CONSTRUCCION DE VEREDAS", 5) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Equal(1, resultado.DescarteDuro);
        Assert.Empty(resultado.TramoBajo);
    }

    [Fact]
    public void TipoPrivado_VaASecundarias_AunqueMatcheeRubroPrioritario()
    {
        // "CO" está en TiposPrivados: aunque el nombre matchea "auditor"
        // (rubro alta compliance), nunca debe entrar a Prioritarias — el
        // tipo privado bypasea la clasificación de rubro por completo.
        var licitaciones = new[] { Licitacion("1-1-CO26", "AUDITORIA GENERAL", 5) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Empty(resultado.Prioritarias);
        var secundaria = Assert.Single(resultado.Secundarias);
        Assert.Null(secundaria.RubroMatch);
        Assert.Null(secundaria.TerminoMatch);
        Assert.Equal("CO", secundaria.Tipo);
    }

    [Fact]
    public void TipoPrivado_TambienSujetoADescarteDuro()
    {
        var licitaciones = new[] { Licitacion("1-1-CO26", "CONSTRUCCION DE VEREDAS", 5) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Equal(1, resultado.DescarteDuro);
        Assert.Empty(resultado.Secundarias);
    }
}
