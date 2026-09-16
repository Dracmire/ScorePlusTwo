using ScorePlusTwo.Pipeline.Infraestructura;
using ScorePlusTwo.Pipeline.Unspsc;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

// Contra el catálogo real de producción (config/catalogo-unspsc.tsv,
// 13.335 filas) — no uno inventado: lo que hay que verificar es que la
// resolución calza con la realidad, no que el código corre. Los códigos de
// control (43222503, 94131603, 80111504) ya se verificaron a mano contra
// el Excel fuente durante la investigación de la etapa 3.
public class CatalogoUnspscTests
{
    private static readonly CatalogoUnspsc Catalogo = CatalogoUnspsc.CargarDesdeArchivo(
        Path.Combine(RutaRepo.Resolver(), "config", "catalogo-unspsc.tsv"));

    [Theory]
    [InlineData(43222503, 'G')] // Vulnerability Assessment Security Equipment
    [InlineData(94131603, 'J')] // Legal assistance services
    [InlineData(80111504, 'J')] // Labor training or development (598-16-LE26, verificado contra la API real)
    public void ResolverRaiz_CodigoConocido_ResuelveLaRaizVerificada(int codigoProducto, char raizEsperada)
    {
        Assert.Equal(raizEsperada, Catalogo.ResolverRaiz(codigoProducto));
    }

    [Fact]
    public void ResolverRaiz_ProfundidadNoUniforme_NoAsumeUnCaminoFijo()
    {
        // 80111504 resuelve en 4 saltos (80111504 -> 80111500 -> 80110000 ->
        // 80000000 -> J); 43222503 y 94131603 tienen profundidades distintas
        // — si el caminador asumiera una profundidad fija, alguno fallaría.
        Assert.NotNull(Catalogo.ResolverRaiz(43222503));
        Assert.NotNull(Catalogo.ResolverRaiz(94131603));
        Assert.NotNull(Catalogo.ResolverRaiz(80111504));
    }

    [Fact]
    public void ResolverRaiz_CadenaRotaDelSegmento57_DevuelveNullSinLanzar()
    {
        // Emergency IT equipment kits (57888100): fila real del catálogo
        // cuya cadena de Parent key no termina en ninguna de las 10 raíces
        // — hallazgo documentado en la investigación de la etapa 3 (18
        // filas del segmento 57 con este defecto).
        Assert.Null(Catalogo.ResolverRaiz(57888100));
    }

    [Fact]
    public void ResolverRaiz_CodigoNoExisteEnElCatalogo_DevuelveNullSinLanzar()
    {
        Assert.Null(Catalogo.ResolverRaiz(99999999));
    }
}
