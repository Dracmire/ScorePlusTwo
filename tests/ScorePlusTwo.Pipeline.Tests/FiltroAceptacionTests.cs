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
// CodigoProducto reales ya confirmados para los códigos de este fixture
// (F2, 2026-09-16: datos reales, nunca inventados — ver plan de sesión).
// Ese archivo empieza vacío: se va poblando a medida que se consigue el
// detalle real de cada código desde la API. Mientras un código no tenga
// entrada, su licitación cae en Secundarias como PendienteEnriquecimiento
// (nunca se inventa una clasificación para que un test pase).
//
// No afirma "exactamente 4" de forma rígida (eso invita a sobreajustar el
// filtro a un solo día de datos); verifica que el total no se desborde, y
// dejará los 5 falsos positivos ya identificados fuera de Prioritarias
// por su motivo de negocio en cuanto haya CodigoProducto real para
// evaluarlos.
public class FiltroAceptacionTests
{
    private static readonly string[] CodigosEsperados =
    {
        "2981-256-LE26", "732434-20-LP26", "85-41-LE26", "734-50-LE26",
    };

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

    // Bloqueado (F2, 2026-09-16): con rubro evaluándose solo sobre
    // UnspscEstado.Servicio, estas 4 candidatas necesitan una entrada real
    // en tests/fixtures/cache-unspsc-2026-09-03.json para volver a
    // aparecer en Prioritarias — hoy ese archivo está vacío (falta pedir
    // el detalle real de estos 4 códigos, ver plan de sesión). Sin eso,
    // caen en Secundarias como PendienteEnriquecimiento, que es el
    // comportamiento correcto, no un bug.
    [Fact(Skip = "Bloqueado: faltan las 4 entradas reales en tests/fixtures/cache-unspsc-2026-09-03.json (F2).")]
    public void LasCuatroCandidatasConocidasEstanPresentes()
    {
        var codigosObtenidos = Resultado.Prioritarias.Select(c => c.Origen.CodigoExterno).ToList();
        foreach (var codigo in CodigosEsperados)
        {
            Assert.Contains(codigo, codigosObtenidos);
        }
    }

    [Fact]
    public void ElTotalDeCandidatasNoSeDesborda()
    {
        Assert.True(
            Resultado.Prioritarias.Count <= 8,
            $"Se esperaban <=8 candidatas, se obtuvieron {Resultado.Prioritarias.Count}: " +
            string.Join(", ", Resultado.Prioritarias.Select(c => c.Origen.CodigoExterno)));
    }

    // F2 (2026-09-16): estos 5 tests quedan bloqueados hasta tener el
    // CodigoProducto real de cada código (pedido al usuario, ver plan de
    // sesión) para agregar su entrada a
    // tests/fixtures/cache-unspsc-2026-09-03.json y reescribirlos
    // verificando la clasificación UNSPSC real, no un match de palabra.
    [Fact(Skip = "Bloqueado: falta CodigoProducto real de 1305541-3-LE26 en tests/fixtures/cache-unspsc-2026-09-03.json (F2).")]
    public void ArriendoDeSoftware_EsBien_NoServicio()
    {
        // 1305541-3-LE26 "ARRIENDO DE SOFTWARE DE INVENTARIO" — arrendar
        // software es compra de un bien/licencia, no un servicio profesional.
        Assert.DoesNotContain(Resultado.Prioritarias, c => c.Origen.CodigoExterno == "1305541-3-LE26");
    }

    [Fact(Skip = "Bloqueado: falta CodigoProducto real de 3797-48-LE26 en tests/fixtures/cache-unspsc-2026-09-03.json (F2).")]
    public void ServidorInstitucional_EsHardware()
    {
        // 3797-48-LE26 "ADQUISICION SERVIDOR INSTITUCIONAL" — compra de
        // hardware, no un servicio de TI.
        Assert.DoesNotContain(Resultado.Prioritarias, c => c.Origen.CodigoExterno == "3797-48-LE26");
    }

    [Fact(Skip = "Bloqueado: falta CodigoProducto real de 434-104-LE26 en tests/fixtures/cache-unspsc-2026-09-03.json (F2).")]
    public void ServidorDeDatos_EsHardware_AunqueMencioneInformatica()
    {
        // 434-104-LE26 "SERVIDOR DE DATOS SEGUN FORMULARIO N°14 INFORMATICA"
        // — matchea "informátic", pero es una compra de hardware.
        Assert.DoesNotContain(Resultado.Prioritarias, c => c.Origen.CodigoExterno == "434-104-LE26");
    }

    [Fact(Skip = "Bloqueado: falta CodigoProducto real de 598-20-LE26 en tests/fixtures/cache-unspsc-2026-09-03.json (F2).")]
    public void LicenciasDeSoftware_EsBien_NoServicio()
    {
        // 598-20-LE26 "Adquisición Licencias de Software para DIPRECA".
        Assert.DoesNotContain(Resultado.Prioritarias, c => c.Origen.CodigoExterno == "598-20-LE26");
    }

    [Fact(Skip = "Bloqueado: falta CodigoProducto real de 1596-45-LE26 en tests/fixtures/cache-unspsc-2026-09-03.json (F2). " +
        "Sin esa entrada nunca resuelve a Servicio, así que rubro nunca se evalúa y RubroMatch queda null.")]
    public void CapacitacionIA_RubroSecundario_VaASecundarias()
    {
        // 1596-45-LE26 "CAPACITACIÓN EN INTELIGENCIA ARTIFICIAL" — el rubro
        // "ia" es señal de mercado (sin partner para atenderlo), prioridad
        // "secundaria": no debe aparecer en Prioritarias, pero sí en
        // Secundarias, con su rubro_match conservado.
        Assert.DoesNotContain(Resultado.Prioritarias, c => c.Origen.CodigoExterno == "1596-45-LE26");
        Assert.Contains(Resultado.Secundarias, c => c.Origen.CodigoExterno == "1596-45-LE26" && c.RubroMatch == "ia");
    }
}
