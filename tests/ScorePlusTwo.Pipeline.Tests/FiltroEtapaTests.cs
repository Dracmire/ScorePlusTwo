using ScorePlusTwo.Pipeline.Filtro;
using ScorePlusTwo.Pipeline.Modelos;
using ScorePlusTwo.Pipeline.Unspsc;
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

    // Catálogo sintético mínimo, solo para probar la mecánica de la
    // compuerta UNSPSC (no verifica contra la realidad — eso lo hace
    // CatalogoUnspscTests/ClasificadorUnspscTests contra el catálogo real):
    // 900001 -> raíz G (bien), 900002 -> raíz J (servicio).
    private static readonly CatalogoUnspsc CatalogoDePrueba = CatalogoUnspsc.CargarDesdeTexto(
        "Key\tParentKey\tCode\tTitulo\n" +
        "1\t\tG\tBienes\n" +
        "2\t\tJ\tServicios\n" +
        "3\t1\t900001\tBien de prueba\n" +
        "4\t2\t900002\tServicio de prueba\n");

    private const int CodigoProductoBien = 900001;
    private const int CodigoProductoServicio = 900002;
    private const int CodigoProductoSinCatalogo = 999999;

    private static Dictionary<string, EntradaCacheUnspsc> CacheVacio() => new();

    private static Dictionary<string, EntradaCacheUnspsc> CacheConCodigoProducto(string codigo, int codigoProducto) =>
        new() { [codigo] = new EntradaCacheUnspsc(codigo, new List<ItemUnspscCache> { new(codigoProducto, null) }, DateTime.UtcNow) };

    private static ResultadoFiltro EjecutarFiltro(
        IEnumerable<LicitacionRaw> licitaciones, Criterios criterios, Dictionary<string, EntradaCacheUnspsc>? cache = null)
    {
        var sobrevivientes = FiltroLicitaciones.FiltrarHastaDescarteDuro(licitaciones, criterios);
        return FiltroLicitaciones.ClasificarYFiltrarRubro(sobrevivientes, criterios, cache ?? CacheVacio(), CatalogoDePrueba);
    }

    private static LicitacionRaw Licitacion(string codigo, string nombre, int estado) =>
        new(codigo, nombre, estado, new DateTime(2026, 9, 10));

    [Fact]
    public void EstadoDesconocido_NoCrashea_YQuedaFueraDelFiltro()
    {
        var licitaciones = new[] { Licitacion("1-1-LE26", "AUDITORIA GENERAL", 999) };

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba());

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

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba());

        Assert.Equal(3, resultado.TrasEstado);
        Assert.Equal(0, resultado.TrasTipo);
        Assert.Empty(resultado.Prioritarias);
    }

    [Fact]
    public void CodigoMalformado_NoCrashea_YNoMatchea()
    {
        var licitaciones = new[] { Licitacion("SINGUION", "AUDITORIA GENERAL", 5) };

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba());

        Assert.Equal(0, resultado.TrasTipo);
        Assert.Empty(resultado.Prioritarias);
    }

    [Fact]
    public void DescarteDuroGanaSiempre_AunqueTambienMatcheeRubro()
    {
        var licitaciones = new[] { Licitacion("1-1-LE26", "AUDITORIA DE VEHICULOS MENORES", 5) };

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba());

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

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba());

        Assert.Equal(1, resultado.DescarteDuro);
        Assert.DoesNotContain(resultado.Prioritarias, c => c.Origen.CodigoExterno == "1-1-LE26");
        Assert.DoesNotContain(resultado.Secundarias, c => c.Origen.CodigoExterno == "1-1-LE26");
        Assert.DoesNotContain(resultado.TramoBajo, c => c.Origen.CodigoExterno == "1-1-LE26");
    }

    [Fact]
    public void SinCacheUnspsc_NoPasaPorRubro_VaASecundariasComoPendiente()
    {
        // El núcleo del rediseño F2: matchear un rubro alta por palabra ya
        // no basta para entrar a Prioritarias — sin una entrada de cache
        // que confirme UnspscEstado.Servicio, ni siquiera se evalúa rubro.
        var licitaciones = new[] { Licitacion("1-1-LE26", "SERVICIO DE AUDITORIA EXTERNA", 5) };

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba(), CacheVacio());

        Assert.Empty(resultado.Prioritarias);
        var secundaria = Assert.Single(resultado.Secundarias);
        Assert.Null(secundaria.RubroMatch);
        Assert.Equal(UnspscEstado.PendienteEnriquecimiento, secundaria.UnspscEstado);
    }

    [Fact]
    public void CacheConfirmaBien_NuncaEntraAPrioritarias_AunqueMatcheeRubroPorPalabra()
    {
        // Abstracción del caso real software/servidor: el nombre matchea
        // "auditor" (rubro alta), pero UNSPSC confirma que es un bien. Ya no
        // hace falta exclusiones_rubro para esto — el bien nunca llega a
        // evaluarse contra rubro.
        var licitaciones = new[] { Licitacion("1-1-LE26", "ARRIENDO DE AUDITOR DE PRUEBA", 5) };
        var cache = CacheConCodigoProducto("1-1-LE26", CodigoProductoBien);

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba(), cache);

        Assert.Empty(resultado.Prioritarias);
        var secundaria = Assert.Single(resultado.Secundarias);
        Assert.Null(secundaria.RubroMatch);
        Assert.Equal(UnspscEstado.Bien, secundaria.UnspscEstado);
    }

    [Fact]
    public void CacheSinResolver_VaASecundarias()
    {
        // La API respondió (hay entrada de cache) pero el CodigoProducto no
        // está en el catálogo — bucket explícito, nunca un fallback
        // silencioso ni una promoción a Prioritarias.
        var licitaciones = new[] { Licitacion("1-1-LE26", "SERVICIO DE AUDITORIA EXTERNA", 5) };
        var cache = CacheConCodigoProducto("1-1-LE26", CodigoProductoSinCatalogo);

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba(), cache);

        Assert.Empty(resultado.Prioritarias);
        var secundaria = Assert.Single(resultado.Secundarias);
        Assert.Null(secundaria.RubroMatch);
        Assert.Equal(UnspscEstado.SinResolver, secundaria.UnspscEstado);
    }

    [Fact]
    public void MatchPorSubstring_NoPalabraCompleta()
    {
        var licitaciones = new[] { Licitacion("1-1-LE26", "SERVICIO DE AUDITORIAS EXTERNAS", 5) };
        var cache = CacheConCodigoProducto("1-1-LE26", CodigoProductoServicio);

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba(), cache);

        var candidata = Assert.Single(resultado.Prioritarias);
        Assert.Equal("compliance", candidata.RubroMatch);
        Assert.Equal("auditor", candidata.TerminoMatch);
        Assert.Equal(UnspscEstado.Servicio, candidata.UnspscEstado);
    }

    [Fact]
    public void RubroSecundario_VaASecundarias_NoAPrioritarias()
    {
        var licitaciones = new[] { Licitacion("1-1-LE26", "INSTALACION DE CAMARA DE SEGURIDAD", 5) };
        var cache = CacheConCodigoProducto("1-1-LE26", CodigoProductoServicio);

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba(), cache);

        Assert.Empty(resultado.Prioritarias);
        var secundaria = Assert.Single(resultado.Secundarias);
        Assert.Equal("vigilancia", secundaria.RubroMatch);
        Assert.Equal("camara", secundaria.TerminoMatch);
    }

    [Fact]
    public void SinNingunRubro_VaASecundarias_ComoInventarioDeProspeccion()
    {
        var licitaciones = new[] { Licitacion("1-1-LE26", "SERVICIO GENERICO SIN RUBRO CONOCIDO", 5) };
        var cache = CacheConCodigoProducto("1-1-LE26", CodigoProductoServicio);

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba(), cache);

        Assert.Empty(resultado.Prioritarias);
        var secundaria = Assert.Single(resultado.Secundarias);
        Assert.Null(secundaria.RubroMatch);
        Assert.Null(secundaria.TerminoMatch);
        Assert.Equal(UnspscEstado.Servicio, secundaria.UnspscEstado);
    }

    [Fact]
    public void TipoL1_VaATramoBajo_NoSeMezclaConElResto()
    {
        var licitaciones = new[] { Licitacion("1-1-L126", "AUDITORIA GENERAL", 5) };

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba());

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

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba());

        Assert.Equal(1, resultado.DescarteDuro);
        Assert.Empty(resultado.TramoBajo);
    }

    [Fact]
    public void TipoPrivado_VaASecundarias_AunqueMatcheeRubroPrioritario()
    {
        // "CO" está en TiposPrivados: aunque el nombre matchea "auditor"
        // (rubro alta compliance), nunca debe entrar a Prioritarias — el
        // tipo privado bypasea enriquecimiento y clasificación de rubro por
        // completo, incluso con una entrada de cache que confirme servicio.
        var licitaciones = new[] { Licitacion("1-1-CO26", "AUDITORIA GENERAL", 5) };
        var cache = CacheConCodigoProducto("1-1-CO26", CodigoProductoServicio);

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba(), cache);

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

        var resultado = EjecutarFiltro(licitaciones, CriteriosDePrueba());

        Assert.Equal(1, resultado.DescarteDuro);
        Assert.Empty(resultado.Secundarias);
    }
}
