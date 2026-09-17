using ScorePlusTwo.Pipeline.Infraestructura;
using ScorePlusTwo.Pipeline.Modelos;
using ScorePlusTwo.Pipeline.Unspsc;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

public class ClasificadorUnspscTests
{
    private static readonly CatalogoUnspsc Catalogo = CatalogoUnspsc.CargarDesdeArchivo(
        Path.Combine(RutaRepo.Resolver(), "config", "catalogo-unspsc.tsv"));

    private static readonly IReadOnlySet<string> SinFamiliasAmbiguas = new HashSet<string>();
    private static readonly IReadOnlySet<string> FamiliaSoftwareAmbigua = new HashSet<string> { "4323" };

    private const int CodigoServicio = 94131603; // Legal assistance services -> J
    private const int CodigoBien = 43222503; // Vulnerability Assessment Security Equipment -> G
    private const int CodigoCadenaRota = 57888100; // Emergency IT equipment kits -> sin raíz
    private const int CodigoSoftwareAmbiguo = 43231512; // License management software -> G, familia 4323 (598-20-LE26 real)

    [Fact]
    public void Clasificar_SinEntradaDeCache_EsPendienteEnriquecimiento()
    {
        Assert.Equal(UnspscEstado.PendienteEnriquecimiento, ClasificadorUnspsc.Clasificar(null, Catalogo, SinFamiliasAmbiguas));
    }

    [Fact]
    public void Clasificar_UnItemResuelveAServicio_EsServicio()
    {
        var entrada = new EntradaCacheUnspsc("1-1-LE26", new List<ItemUnspscCache>
        {
            new(CodigoServicio, "94131600"),
        }, DateTime.UtcNow);

        Assert.Equal(UnspscEstado.Servicio, ClasificadorUnspsc.Clasificar(entrada, Catalogo, SinFamiliasAmbiguas));
    }

    [Fact]
    public void Clasificar_UnItemResuelveABien_EsBien()
    {
        var entrada = new EntradaCacheUnspsc("2-1-LE26", new List<ItemUnspscCache>
        {
            new(CodigoBien, "43222500"),
        }, DateTime.UtcNow);

        Assert.Equal(UnspscEstado.Bien, ClasificadorUnspsc.Clasificar(entrada, Catalogo, SinFamiliasAmbiguas));
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

        Assert.Equal(UnspscEstado.Servicio, ClasificadorUnspsc.Clasificar(entrada, Catalogo, SinFamiliasAmbiguas));
    }

    [Fact]
    public void Clasificar_NingunItemResuelve_EsSinResolver()
    {
        var entrada = new EntradaCacheUnspsc("4-1-LE26", new List<ItemUnspscCache>
        {
            new(CodigoCadenaRota, "57888100"),
        }, DateTime.UtcNow);

        Assert.Equal(UnspscEstado.SinResolver, ClasificadorUnspsc.Clasificar(entrada, Catalogo, SinFamiliasAmbiguas));
    }

    [Fact]
    public void Clasificar_CodigoProductoNulo_EsSinResolver()
    {
        // La API devolvió el detalle pero sin CodigoProducto (Categoria vacía).
        var entrada = new EntradaCacheUnspsc("5-1-LE26", new List<ItemUnspscCache>
        {
            new(null, null),
        }, DateTime.UtcNow);

        Assert.Equal(UnspscEstado.SinResolver, ClasificadorUnspsc.Clasificar(entrada, Catalogo, SinFamiliasAmbiguas));
    }

    [Fact]
    public void Clasificar_SinItems_EsSinResolver()
    {
        var entrada = new EntradaCacheUnspsc("6-1-LE26", new List<ItemUnspscCache>(), DateTime.UtcNow);

        Assert.Equal(UnspscEstado.SinResolver, ClasificadorUnspsc.Clasificar(entrada, Catalogo, SinFamiliasAmbiguas));
    }

    [Fact]
    public void Clasificar_FamiliaAmbiguaSola_EsRevisionManual()
    {
        // 598-20-LE26 real: "Adquisición Licencias de Software para
        // DIPRECA" — 43231512 resuelve a G (bien) pero está en la familia
        // 4323 (Software), donde la clase de 8 dígitos no distingue
        // arriendo con soporte de compra pura de licencias.
        var entrada = new EntradaCacheUnspsc("7-1-LE26", new List<ItemUnspscCache>
        {
            new(CodigoSoftwareAmbiguo, "43231500"),
        }, DateTime.UtcNow);

        Assert.Equal(UnspscEstado.RevisionManual, ClasificadorUnspsc.Clasificar(entrada, Catalogo, FamiliaSoftwareAmbigua));
    }

    [Fact]
    public void Clasificar_SinFamiliasConfiguradas_FamiliaAmbiguaNoAplica_EsBien()
    {
        // Mismo código de arriba, pero sin ninguna familia configurada como
        // ambigua (SinFamiliasAmbiguas) — debe resolver como cualquier
        // otro bien, sin el tratamiento especial.
        var entrada = new EntradaCacheUnspsc("8-1-LE26", new List<ItemUnspscCache>
        {
            new(CodigoSoftwareAmbiguo, "43231500"),
        }, DateTime.UtcNow);

        Assert.Equal(UnspscEstado.Bien, ClasificadorUnspsc.Clasificar(entrada, Catalogo, SinFamiliasAmbiguas));
    }

    [Fact]
    public void Clasificar_FamiliaAmbiguaMasOtroItemServicio_GanaServicio()
    {
        // Precedencia: Servicio > RevisionManual > Bien > SinResolver —
        // cualquier ítem de servicio basta, igual que ya vale para Bien.
        var entrada = new EntradaCacheUnspsc("9-1-LE26", new List<ItemUnspscCache>
        {
            new(CodigoSoftwareAmbiguo, "43231500"),
            new(CodigoServicio, "94131600"),
        }, DateTime.UtcNow);

        Assert.Equal(UnspscEstado.Servicio, ClasificadorUnspsc.Clasificar(entrada, Catalogo, FamiliaSoftwareAmbigua));
    }

    [Fact]
    public void Clasificar_FamiliaAmbiguaMasOtroItemBien_GanaRevisionManual()
    {
        // Precedencia: RevisionManual > Bien — la presencia de CUALQUIER
        // ítem de familia ambigua basta para marcar la licitación entera
        // para revisión, aunque otro ítem resuelva a un bien normal.
        var entrada = new EntradaCacheUnspsc("10-1-LE26", new List<ItemUnspscCache>
        {
            new(CodigoSoftwareAmbiguo, "43231500"),
            new(CodigoBien, "43222500"),
        }, DateTime.UtcNow);

        Assert.Equal(UnspscEstado.RevisionManual, ClasificadorUnspsc.Clasificar(entrada, Catalogo, FamiliaSoftwareAmbigua));
    }
}
