using ScorePlusTwo.Pipeline.Filtro;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

public class CodigoExternoParserTests
{
    [Theory]
    [InlineData("2981-256-LE26", "LE", "26")]
    [InlineData("1004-46-LP26", "LP", "26")]
    [InlineData("1110404-175-L126", "L1", "26")]
    [InlineData("1057898-70-LR26", "LR", "26")]
    public void CasosReales_SeParseanCorrectamente(string codigo, string tipoEsperado, string anioEsperado)
    {
        var exito = CodigoExternoParser.TryExtraerTipoAnio(codigo, out var tipo, out var anio);

        Assert.True(exito);
        Assert.Equal(tipoEsperado, tipo);
        Assert.Equal(anioEsperado, anio);
    }

    [Theory]
    [InlineData("SINGUION")]
    [InlineData("1-1-L")]
    [InlineData("")]
    public void CodigoMalformado_RetornaFalse_SinExcepcion(string codigo)
    {
        var exito = CodigoExternoParser.TryExtraerTipoAnio(codigo, out _, out _);

        Assert.False(exito);
    }
}
