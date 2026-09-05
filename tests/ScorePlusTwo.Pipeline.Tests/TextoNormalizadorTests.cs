using ScorePlusTwo.Pipeline.Filtro;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

public class TextoNormalizadorTests
{
    [Theory]
    [InlineData("AUDITORÍA", "auditoria")]
    [InlineData("Informática", "informatica")]
    [InlineData("PROTECCIÓN DE DATOS", "proteccion de datos")]
    [InlineData("jurídico", "juridico")]
    public void QuitaTildesYPasaAMinusculas(string entrada, string esperado)
    {
        Assert.Equal(esperado, TextoNormalizador.Normalizar(entrada));
    }

    [Fact]
    public void ColapsaEneConVirgulillaAEneSimple()
    {
        // Simplificación intencional de F1 (documentada en FiltroLicitaciones):
        // NFKD descompone "ñ" en "n" + tilde combinante, que se elimina junto
        // con las demás marcas diacríticas.
        Assert.Equal("ano", TextoNormalizador.Normalizar("año"));
    }
}
