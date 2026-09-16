using ScorePlusTwo.Pipeline.Infraestructura;
using ScorePlusTwo.Pipeline.Modelos;
using ScorePlusTwo.Pipeline.Unspsc;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

public class ClasificadorUnspscTests
{
    private static readonly CatalogoUnspsc Catalogo = CatalogoUnspsc.CargarDesdeArchivo(
        Path.Combine(RutaRepo.Resolver(), "config", "catalogo-unspsc.tsv"));

    private const int CodigoServicio = 94131603; // Legal assistance services -> J
    private const int CodigoBien = 43222503; // Vulnerability Assessment Security Equipment -> G
    private const int CodigoCadenaRota = 57888100; // Emergency IT equipment kits -> sin raíz

    [Fact]
    public void Clasificar_SinEntradaDeCache_EsPendienteEnriquecimiento()
    {
        Assert.Equal(UnspscEstado.PendienteEnriquecimiento, ClasificadorUnspsc.Clasificar(null, Catalogo));
    }

    [Fact]
    public void Clasificar_UnItemResuelveAServicio_EsServicio()
    {
        var entrada = new EntradaCacheUnspsc("1-1-LE26", new List<ItemUnspscCache>
        {
            new(CodigoServicio, "94131600"),
        }, DateTime.UtcNow);

        Assert.Equal(UnspscEstado.Servicio, ClasificadorUnspsc.Clasificar(entrada, Catalogo));
    }

    [Fact]
    public void Clasificar_UnItemResuelveABien_EsBien()
    {
        var entrada = new EntradaCacheUnspsc("2-1-LE26", new List<ItemUnspscCache>
        {
            new(CodigoBien, "43222500"),
        }, DateTime.UtcNow);

        Assert.Equal(UnspscEstado.Bien, ClasificadorUnspsc.Clasificar(entrada, Catalogo));
    }

    [Fact]
    public void Clasificar_MezclaDeBienYServicio_EsServicio()
    {
        // El caso real que motivó este rediseño: "Convenio Suministro de
        // Servicio de Fotocopiado ... con Entrega de Equipos"
        // (1057548-21-LE26) — un ítem de servicio y un ítem de bien en la
        // misma licitación. Cualquier ítem de servicio basta para que la
        // licitación completa sea una oportunidad de servicio real.
        var entrada = new EntradaCacheUnspsc("3-1-LE26", new List<ItemUnspscCache>
        {
            new(CodigoBien, "43222500"),
            new(CodigoServicio, "94131600"),
        }, DateTime.UtcNow);

        Assert.Equal(UnspscEstado.Servicio, ClasificadorUnspsc.Clasificar(entrada, Catalogo));
    }

    [Fact]
    public void Clasificar_NingunItemResuelve_EsSinResolver()
    {
        var entrada = new EntradaCacheUnspsc("4-1-LE26", new List<ItemUnspscCache>
        {
            new(CodigoCadenaRota, "57888100"),
        }, DateTime.UtcNow);

        Assert.Equal(UnspscEstado.SinResolver, ClasificadorUnspsc.Clasificar(entrada, Catalogo));
    }

    [Fact]
    public void Clasificar_CodigoProductoNulo_EsSinResolver()
    {
        // La API devolvió el detalle pero sin CodigoProducto (Categoria vacía).
        var entrada = new EntradaCacheUnspsc("5-1-LE26", new List<ItemUnspscCache>
        {
            new(null, null),
        }, DateTime.UtcNow);

        Assert.Equal(UnspscEstado.SinResolver, ClasificadorUnspsc.Clasificar(entrada, Catalogo));
    }

    [Fact]
    public void Clasificar_SinItems_EsSinResolver()
    {
        var entrada = new EntradaCacheUnspsc("6-1-LE26", new List<ItemUnspscCache>(), DateTime.UtcNow);

        Assert.Equal(UnspscEstado.SinResolver, ClasificadorUnspsc.Clasificar(entrada, Catalogo));
    }
}
