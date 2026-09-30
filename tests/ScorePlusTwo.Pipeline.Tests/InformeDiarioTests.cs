using System.Reflection;
using ScorePlusTwo.Pipeline.Modelos;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

// InformeDiario.Fusionar (2026-09-30) reemplaza el `with { ... }` inline que
// antes vivía en Program.ActualizarSerieInformes -- ver el follow-up
// "informes.json pisado por un segundo workflow_dispatch el mismo día" para
// el caso real que motivó esto: un workflow_dispatch manual sin flags,
// disparado el mismo día que ya había corrido el cron, pisó Prioritarias
// (2->0) y EnriquecidosHoy (324->0) de la entrada del 2026-09-29 porque el
// filtro de acumulados ve distinto estado en la segunda pasada.
public class InformeDiarioTests
{
    private static InformeDiario Informe(
        int prioritarias = 0, int trasRegion = 0, int reverificadasHoy = 0, int llamadasReverificacion = 0,
        InformeFunnel? barridoActivas = null) => new(
        Fecha: new DateOnly(2026, 9, 29),
        Total: 1198, TrasEstado: 478, TrasTipo: 469, TrasRegion: trasRegion, DescarteDuro: 73,
        Prioritarias: prioritarias, Secundarias: 331, TramoBajo: 63,
        NuevasPrioritarias: 2, NuevasSecundarias: 331, NuevasTramoBajo: 63,
        BarridoActivas: barridoActivas,
        ReverificadasHoy: reverificadasHoy, LlamadasReverificacion: llamadasReverificacion);

    // 1. Descubrimiento congelado en la primera corrida exitosa de la
    // fecha; seguimiento/cuota sumado entre corridas -- caso real del
    // 2026-09-29: la segunda pasada calculó Prioritarias=0/TrasRegion=40
    // (contaminados por el filtro de acumulados), pero el resultado debe
    // conservar los valores de la primera corrida.
    [Fact]
    public void Fusionar_CongelaDescubrimientoYSumaSeguimiento()
    {
        var existente = Informe(prioritarias: 2, trasRegion: 42, reverificadasHoy: 100, llamadasReverificacion: 80);
        var nuevo = Informe(prioritarias: 0, trasRegion: 40, reverificadasHoy: 236, llamadasReverificacion: 236);

        var resultado = InformeDiario.Fusionar(existente, nuevo);

        Assert.Equal(2, resultado.Prioritarias);
        Assert.Equal(42, resultado.TrasRegion);
        Assert.Equal(336, resultado.ReverificadasHoy);
        Assert.Equal(316, resultado.LlamadasReverificacion);
    }

    // 2. BarridoActivas: se completa si venía null en la primera corrida,
    // y una vez no-null, queda congelado igual que el resto del
    // descubrimiento (no se reemplaza por el de una tercera corrida).
    [Fact]
    public void Fusionar_CompletaBarridoActivasSiVeniaNull_LuegoLoCongela()
    {
        var primeraCorridaSinActivas = Informe(barridoActivas: null);
        var segundaCorridaConActivas = Informe(barridoActivas: new InformeFunnel(
            Total: 4826, TrasEstado: 4768, TrasTipo: 4768, TrasRegion: 40, DescarteDuro: 1035,
            Prioritarias: 1, Secundarias: 3524, TramoBajo: 267,
            NuevasPrioritarias: 0, NuevasSecundarias: 5, NuevasTramoBajo: 2));

        var resultadoSegunda = InformeDiario.Fusionar(primeraCorridaSinActivas, segundaCorridaConActivas);
        Assert.NotNull(resultadoSegunda.BarridoActivas);
        Assert.Equal(1, resultadoSegunda.BarridoActivas!.Prioritarias);

        var terceraCorridaConOtroActivas = Informe(barridoActivas: new InformeFunnel(
            Total: 9999, TrasEstado: 1, TrasTipo: 1, TrasRegion: 1, DescarteDuro: 1,
            Prioritarias: 99, Secundarias: 1, TramoBajo: 1,
            NuevasPrioritarias: 0, NuevasSecundarias: 0, NuevasTramoBajo: 0));

        var resultadoTercera = InformeDiario.Fusionar(resultadoSegunda, terceraCorridaConOtroActivas);
        Assert.Equal(1, resultadoTercera.BarridoActivas!.Prioritarias); // sigue el de la segunda, no el de la tercera
    }

    // 3. Guardia por reflexión: toda propiedad de InformeDiario (salvo
    // Fecha) debe estar en exactamente una de CamposDescubrimiento/
    // CamposSeguimiento -- un campo nuevo sin clasificar no puede quedar
    // congelado por omisión en Fusionar.
    [Fact]
    public void TodaPropiedadEstaClasificadaEnExactamenteUnaCategoria()
    {
        var propiedades = typeof(InformeDiario).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(nombre => nombre != nameof(InformeDiario.Fecha))
            .ToList();

        Assert.NotEmpty(propiedades);

        foreach (var nombre in propiedades)
        {
            var enDescubrimiento = InformeDiario.CamposDescubrimiento.Contains(nombre);
            var enSeguimiento = InformeDiario.CamposSeguimiento.Contains(nombre);

            Assert.True(
                enDescubrimiento ^ enSeguimiento,
                $"La propiedad '{nombre}' debe estar en exactamente una de CamposDescubrimiento/CamposSeguimiento " +
                $"(está en descubrimiento={enDescubrimiento}, en seguimiento={enSeguimiento}).");
        }
    }
}
