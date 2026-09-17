using ScorePlusTwo.Pipeline.Filtro;
using ScorePlusTwo.Pipeline.Infraestructura;
using ScorePlusTwo.Pipeline.Modelos;
using ScorePlusTwo.Pipeline.Persistencia;
using ScorePlusTwo.Pipeline.Unspsc;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

// El gate: corre el filtro real con el config/criterios.json real (el que se
// va a shippear) contra tests/fixtures/2026-09-03.json (1.172 registros
// reales), clasificando UNSPSC con el catálogo real y con
// tests/fixtures/cache-unspsc-2026-09-03.json — un cache de solo los
// CodigoProducto/RegionUnidad reales ya confirmados para los códigos de
// este fixture (F2, 2026-09-17: datos reales, nunca inventados — ver plan
// de sesión). Las 9 entradas de ese archivo están completas.
//
// CAMBIO DE CONTRATO (2026-09-17): el gate original de F1 afirmaba que
// `2981-256-LE26`, `732434-20-LP26`, `85-41-LE26` y `734-50-LE26` eran las
// 4 candidatas legítimas — correcto para un filtro de solo palabras, pero
// nunca verificó que fueran oportunidades reales. Con datos reales de
// UNSPSC + región, sabemos que solo una de las cuatro lo es:
// `85-41-LE26` cae en familia UNSPSC ambigua (RevisionManual, nunca debe
// auto-promoverse) y `732434-20-LP26`/`734-50-LE26` son servicios reales
// pero fuera de la cobertura geográfica del negocio (Biobío y Aysén,
// respectivamente — el negocio opera en Metropolitana/Valparaíso/
// Coquimbo). Mantener el test original habría validado un contrato que ya
// sabemos que está mal, así que se retira en vez de forzarlo.
public class FiltroAceptacionTests
{
    private static readonly ResultadoFiltro Resultado = CorrerFiltroSobreFixtureReal();

    private static ResultadoFiltro CorrerFiltroSobreFixtureReal()
    {
        var repoRoot = RutaRepo.Resolver();

        var criterios = JsonStore.Cargar<Criterios>(
            Path.Combine(repoRoot, "config", "criterios.json"), JsonOpciones.Config);

        var fixture = JsonStore.Cargar<ListadoLicitacionesResponse>(
            Path.Combine(repoRoot, "tests", "fixtures", "2026-09-03.json"), JsonOpciones.ApiLectura);

        var cacheLista = JsonStore.CargarOPredeterminado(
            Path.Combine(repoRoot, "tests", "fixtures", "cache-unspsc-2026-09-03.json"),
            JsonOpciones.Persistencia, new List<EntradaCacheUnspsc>());
        var cache = cacheLista.ToDictionary(e => e.CodigoExterno);

        var catalogo = CatalogoUnspsc.CargarDesdeArchivo(Path.Combine(repoRoot, "config", "catalogo-unspsc.tsv"));

        var sobrevivientes = FiltroLicitaciones.FiltrarHastaDescarteDuro(fixture.Listado, criterios);
        return FiltroLicitaciones.ClasificarYFiltrarRubro(sobrevivientes, criterios, cache, catalogo);
    }

    [Fact]
    public void ElTotalDeCandidatasNoSeDesborda()
    {
        Assert.True(
            Resultado.Prioritarias.Count <= 8,
            $"Se esperaban <=8 candidatas, se obtuvieron {Resultado.Prioritarias.Count}: " +
            string.Join(", ", Resultado.Prioritarias.Select(c => c.Origen.CodigoExterno)));
    }

    [Fact]
    public void LaCandidataDeServicioEstaPresente()
    {
        // 2981-256-LE26 "CONTRATACIÓN DE SERVICIOS PROFESIONALES DE
        // AUDITORIA EXTERNA PARA LA PDI" — 93151607 (auditoría
        // gubernamental) resuelve a J, región Metropolitana elegible,
        // rubro compliance/auditor de prioridad alta. Única candidata de
        // servicio real que sobrevive región + rubro en este fixture.
        Assert.Contains(Resultado.Prioritarias, c => c.Origen.CodigoExterno == "2981-256-LE26");
    }

    [Fact]
    public void PlataformaItamSam_RevisionManual_SuscripcionDeProducto()
    {
        // 85-41-LE26 "PLATAFORMA INTEGRAL SOPORTE TI CON CONTROL REMOTO" —
        // 43232408 (software desarrollo web) está en la familia UNSPSC
        // 4323 (Software), donde la clase de 8 dígitos no distingue
        // arriendo con soporte de compra pura — nunca se auto-promueve,
        // aunque matchea rubro ti/plataforma (queda como contexto, no como
        // ticket de entrada a Prioritarias).
        Assert.DoesNotContain(Resultado.Prioritarias, c => c.Origen.CodigoExterno == "85-41-LE26");
        var secundaria = Assert.Single(Resultado.Secundarias, c => c.Origen.CodigoExterno == "85-41-LE26");
        Assert.Equal(UnspscEstado.RevisionManual, secundaria.UnspscEstado);
    }

    [Fact]
    public void SysadminAysen_DescartadoPorRegion()
    {
        // 734-50-LE26 "ADQUISICIÓN DE ADMINISTRADOR DE SISTEMAS SYSADMIN"
        // — 80111715 (contratación de personal) resuelve a J: es un
        // servicio real según UNSPSC y matchea rubro ti/sysadmin, pero el
        // comprador está en la Región de Aysén — fuera de Metropolitana/
        // Valparaíso/Coquimbo, la cobertura real del negocio. Región nunca
        // corta antes de rubro (corrección explícita del usuario,
        // 2026-09-17): el RubroMatch queda poblado igual, visible para si
        // la cobertura geográfica cambia algún día.
        Assert.DoesNotContain(Resultado.Prioritarias, c => c.Origen.CodigoExterno == "734-50-LE26");
        var secundaria = Assert.Single(Resultado.Secundarias, c => c.Origen.CodigoExterno == "734-50-LE26");
        Assert.Equal(UnspscEstado.Servicio, secundaria.UnspscEstado);
        Assert.Equal("ti", secundaria.RubroMatch);
    }

    [Fact]
    public void ServicioInformaticoBiobio_DescartadoPorRegion()
    {
        // 732434-20-LP26 "SERVICIO INFORMÁTICO PARA EL CENTRO DE SANGRE
        // CONC" — 81111501 (servicios informáticos) resuelve a J y
        // matchea rubro ti/informátic, pero el comprador está en la
        // Región del Biobío (dato real trae un espacio final:
        // "Región del Biobío ") — fuera de cobertura, mismo tratamiento
        // que SysadminAysen_DescartadoPorRegion.
        Assert.DoesNotContain(Resultado.Prioritarias, c => c.Origen.CodigoExterno == "732434-20-LP26");
        var secundaria = Assert.Single(Resultado.Secundarias, c => c.Origen.CodigoExterno == "732434-20-LP26");
        Assert.Equal(UnspscEstado.Servicio, secundaria.UnspscEstado);
        Assert.Equal("ti", secundaria.RubroMatch);
    }

    [Fact]
    public void ArriendoDeSoftware_RevisionManual_AmbiguoEnModalidad()
    {
        // 1305541-3-LE26 "ARRIENDO DE SOFTWARE DE INVENTARIO" — 43231508
        // (familia 4323, Software) no distingue arriendo con soporte de
        // compra pura a 8 dígitos. Nunca se auto-promueve a Prioritarias.
        Assert.DoesNotContain(Resultado.Prioritarias, c => c.Origen.CodigoExterno == "1305541-3-LE26");
        var secundaria = Assert.Single(Resultado.Secundarias, c => c.Origen.CodigoExterno == "1305541-3-LE26");
        Assert.Equal(UnspscEstado.RevisionManual, secundaria.UnspscEstado);
    }

    [Fact]
    public void ServidorInstitucional_EsHardware()
    {
        // 3797-48-LE26 "ADQUISICION SERVIDOR INSTITUCIONAL" — 43211501
        // (Servidores) resuelve a G: compra de hardware, nunca un
        // servicio de TI.
        Assert.DoesNotContain(Resultado.Prioritarias, c => c.Origen.CodigoExterno == "3797-48-LE26");
        var secundaria = Assert.Single(Resultado.Secundarias, c => c.Origen.CodigoExterno == "3797-48-LE26");
        Assert.Equal(UnspscEstado.Bien, secundaria.UnspscEstado);
    }

    [Fact]
    public void ServidorDeDatos_EsHardware_AunqueMencioneInformatica()
    {
        // 434-104-LE26 "SERVIDOR DE DATOS SEGUN FORMULARIO N°14
        // INFORMATICA" — matchea "informátic" por palabra, pero 43211501
        // (Servidores) resuelve a G: es hardware, y Bien nunca llega a
        // evaluarse contra rubro. CantidadReclamos: 332 (Municipalidad de
        // Cauquenes) — nuevo récord observado, referencia de calibración
        // para cuando F2 capture ese campo en producción.
        Assert.DoesNotContain(Resultado.Prioritarias, c => c.Origen.CodigoExterno == "434-104-LE26");
        var secundaria = Assert.Single(Resultado.Secundarias, c => c.Origen.CodigoExterno == "434-104-LE26");
        Assert.Equal(UnspscEstado.Bien, secundaria.UnspscEstado);
        Assert.Null(secundaria.RubroMatch);
    }

    [Fact]
    public void LicenciasDeSoftware_RevisionManual_AmbiguoEnModalidad()
    {
        // 598-20-LE26 "Adquisición Licencias de Software para DIPRECA" —
        // 43231512 (familia 4323, Software) comparte clase de 8 dígitos
        // con 1305541-3-LE26 pese a ser un negocio opuesto (compra pura
        // de licencias de terceros vs. arriendo de plataforma con
        // soporte). Nunca se auto-promueve a Prioritarias.
        Assert.DoesNotContain(Resultado.Prioritarias, c => c.Origen.CodigoExterno == "598-20-LE26");
        var secundaria = Assert.Single(Resultado.Secundarias, c => c.Origen.CodigoExterno == "598-20-LE26");
        Assert.Equal(UnspscEstado.RevisionManual, secundaria.UnspscEstado);
    }

    [Fact]
    public void CapacitacionIA_RubroSecundario_VaASecundarias()
    {
        // 1596-45-LE26 "CAPACITACIÓN EN INTELIGENCIA ARTIFICIAL" —
        // 86111604 (Employee education) resuelve a J: servicio real, y
        // matchea rubro "ia" (prioridad "secundaria" — señal de mercado,
        // sin partner para atenderlo): no debe aparecer en Prioritarias,
        // pero sí en Secundarias con su rubro_match conservado.
        Assert.DoesNotContain(Resultado.Prioritarias, c => c.Origen.CodigoExterno == "1596-45-LE26");
        Assert.Contains(Resultado.Secundarias, c => c.Origen.CodigoExterno == "1596-45-LE26" && c.RubroMatch == "ia");
    }
}
