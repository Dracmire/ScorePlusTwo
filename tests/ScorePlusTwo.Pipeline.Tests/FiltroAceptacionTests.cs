using ScorePlusTwo.Pipeline.Filtro;
using ScorePlusTwo.Pipeline.Infraestructura;
using ScorePlusTwo.Pipeline.Modelos;
using ScorePlusTwo.Pipeline.Persistencia;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

// El gate: corre el filtro real con el config/criterios.json real (el que se
// va a shippear) contra tests/fixtures/2026-09-03.json (1.172 registros
// reales). No afirma "exactamente 4" de forma rígida (eso invita a
// sobreajustar el filtro a un solo día de datos); en cambio verifica que las
// 4 candidatas conocidas nunca falten, que el total no se desborde, y que los
// 5 falsos positivos ya identificados y corregidos en config/criterios.json
// se mantengan fuera, cada uno documentado por su motivo de negocio.
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

        return FiltroLicitaciones.Filtrar(fixture.Listado, criterios);
    }

    [Fact]
    public void LasCuatroCandidatasConocidasEstanPresentes()
    {
        var codigosObtenidos = Resultado.Candidatas.Select(c => c.Origen.CodigoExterno).ToList();
        foreach (var codigo in CodigosEsperados)
        {
            Assert.Contains(codigo, codigosObtenidos);
        }
    }

    [Fact]
    public void ElTotalDeCandidatasNoSeDesborda()
    {
        Assert.True(
            Resultado.Candidatas.Count <= 8,
            $"Se esperaban <=8 candidatas, se obtuvieron {Resultado.Candidatas.Count}: " +
            string.Join(", ", Resultado.Candidatas.Select(c => c.Origen.CodigoExterno)));
    }

    [Fact]
    public void ArriendoDeSoftware_EsBien_NoServicio()
    {
        // 1305541-3-LE26 "ARRIENDO DE SOFTWARE DE INVENTARIO" — arrendar
        // software es compra de un bien/licencia, no un servicio profesional.
        Assert.DoesNotContain(Resultado.Candidatas, c => c.Origen.CodigoExterno == "1305541-3-LE26");
    }

    [Fact]
    public void ServidorInstitucional_EsHardware()
    {
        // 3797-48-LE26 "ADQUISICION SERVIDOR INSTITUCIONAL" — compra de
        // hardware, no un servicio de TI.
        Assert.DoesNotContain(Resultado.Candidatas, c => c.Origen.CodigoExterno == "3797-48-LE26");
    }

    [Fact]
    public void ServidorDeDatos_EsHardware_AunqueMencioneInformatica()
    {
        // 434-104-LE26 "SERVIDOR DE DATOS SEGUN FORMULARIO N°14 INFORMATICA"
        // — matchea "informátic" pero la exclusión "servidor" gana siempre.
        Assert.DoesNotContain(Resultado.Candidatas, c => c.Origen.CodigoExterno == "434-104-LE26");
    }

    [Fact]
    public void LicenciasDeSoftware_EsBien_NoServicio()
    {
        // 598-20-LE26 "Adquisición Licencias de Software para DIPRECA".
        Assert.DoesNotContain(Resultado.Candidatas, c => c.Origen.CodigoExterno == "598-20-LE26");
    }

    [Fact]
    public void CapacitacionIA_RubroNoActivo_VaAObservaciones()
    {
        // 1596-45-LE26 "CAPACITACIÓN EN INTELIGENCIA ARTIFICIAL" — el rubro
        // "ia" es señal de mercado (sin partner para atenderlo), no una
        // candidata operable: no debe aparecer en Candidatas, pero sí en
        // Observaciones.
        Assert.DoesNotContain(Resultado.Candidatas, c => c.Origen.CodigoExterno == "1596-45-LE26");
        Assert.Contains(Resultado.Observaciones, o => o.Codigo == "1596-45-LE26" && o.Rubro == "ia");
    }
}
