using ScorePlusTwo.Pipeline.Filtro;
using ScorePlusTwo.Pipeline.Modelos;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

public class FiltroEtapaTests
{
    private static Criterios CriteriosDePrueba() => new(
        Version: "test",
        Tipos: new List<string> { "LE" },
        Estados: new List<int> { 5 },
        Regiones: new List<string>(),
        Rubros: new List<RubroCriterio>
        {
            new("compliance", true, new List<string> { "auditor" }),
            new("vigilancia", false, new List<string> { "camara" }),
        },
        Exclusiones: new List<string> { "vehiculo" });

    private static LicitacionRaw Licitacion(string codigo, string nombre, int estado) =>
        new(codigo, nombre, estado, new DateTime(2026, 9, 10));

    [Fact]
    public void EstadoDesconocido_NoCrashea_YQuedaFueraDelFiltro()
    {
        var licitaciones = new[] { Licitacion("1-1-LE26", "AUDITORIA GENERAL", 999) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Empty(resultado.Candidatas);
        Assert.Equal(0, resultado.TrasEstado);
    }

    [Fact]
    public void TiposDescartados_NoPasan()
    {
        var licitaciones = new[]
        {
            Licitacion("1-1-L126", "AUDITORIA GENERAL", 5),
            Licitacion("1-1-CO26", "AUDITORIA GENERAL", 5),
            Licitacion("1-1-O126", "AUDITORIA GENERAL", 5),
        };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Equal(3, resultado.TrasEstado);
        Assert.Equal(0, resultado.TrasTipo);
        Assert.Empty(resultado.Candidatas);
    }

    [Fact]
    public void CodigoMalformado_NoCrashea_YNoMatchea()
    {
        var licitaciones = new[] { Licitacion("SINGUION", "AUDITORIA GENERAL", 5) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Equal(0, resultado.TrasTipo);
        Assert.Empty(resultado.Candidatas);
    }

    [Fact]
    public void ExclusionesGananSiempre_AunqueTambienMatcheeRubro()
    {
        var licitaciones = new[] { Licitacion("1-1-LE26", "AUDITORIA DE VEHICULOS MENORES", 5) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Equal(1, resultado.Excluidas);
        Assert.Empty(resultado.Candidatas);
        Assert.Empty(resultado.Observaciones);
    }

    [Fact]
    public void MatchPorSubstring_NoPalabraCompleta()
    {
        var licitaciones = new[] { Licitacion("1-1-LE26", "SERVICIO DE AUDITORIAS EXTERNAS", 5) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        var candidata = Assert.Single(resultado.Candidatas);
        Assert.Equal("compliance", candidata.RubroMatch);
        Assert.Equal("auditor", candidata.TerminoMatch);
    }

    [Fact]
    public void RubroInactivo_VaAObservaciones_NoACandidatas()
    {
        var licitaciones = new[] { Licitacion("1-1-LE26", "INSTALACION DE CAMARA DE SEGURIDAD", 5) };

        var resultado = FiltroLicitaciones.Filtrar(licitaciones, CriteriosDePrueba());

        Assert.Empty(resultado.Candidatas);
        var observacion = Assert.Single(resultado.Observaciones);
        Assert.Equal("vigilancia", observacion.Rubro);
        Assert.Equal("camara", observacion.TerminoMatch);
    }
}
