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

    // CalcularFechasAProcesar (2026-10-01, recupero de fechas faltantes en
    // el flujo normal): ver el follow-up correspondiente para el diseño
    // completo. `ConSoloFecha` arma una entrada mínima de InformeDiario para
    // cada fecha dada (los demás campos no importan para esta función, solo
    // se usa `.Fecha`).
    private static InformeDiario ConSoloFecha(DateOnly fecha) => new(
        Fecha: fecha, Total: 0, TrasEstado: 0, TrasTipo: 0, TrasRegion: 0, DescarteDuro: 0,
        Prioritarias: 0, Secundarias: 0, TramoBajo: 0, NuevasPrioritarias: 0, NuevasSecundarias: 0, NuevasTramoBajo: 0,
        BarridoActivas: null);

    // 4. informes.json vacío: la ventana completa cuenta como "faltante" y
    // el resultado son las `tope` fechas más RECIENTES de la ventana (no
    // las más antiguas) -- consecuencia aceptada y documentada del diseño.
    [Fact]
    public void CalcularFechasAProcesar_InformesVacio_DevuelveLasMasRecientesDeLaVentana()
    {
        var ayer = new DateOnly(2026, 10, 1);

        var resultado = InformeDiario.CalcularFechasAProcesar(
            informesExistentes: new List<InformeDiario>(), fechaManual: null, ayer, ventanaDias: 14, tope: 3);

        Assert.Equal(new[] { ayer, ayer.AddDays(-1), ayer.AddDays(-2) }, resultado);
    }

    // 5. Hueco de 1 día normal (ayer ausente, el resto de la ventana
    // presente): caso de todos los días, sin cambio de comportamiento
    // respecto a antes de este follow-up.
    [Fact]
    public void CalcularFechasAProcesar_HuecoDeUnDia_DevuelveSoloAyer()
    {
        var ayer = new DateOnly(2026, 10, 1);
        var existentes = Enumerable.Range(1, 13)
            .Select(i => ConSoloFecha(ayer.AddDays(-i)))
            .ToList();

        var resultado = InformeDiario.CalcularFechasAProcesar(existentes, fechaManual: null, ayer, ventanaDias: 14, tope: 5);

        Assert.Equal(new[] { ayer }, resultado);
    }

    // 6. Ya al día: toda la ventana ya tiene entrada -- no hay nada que recuperar.
    [Fact]
    public void CalcularFechasAProcesar_YaAlDia_DevuelveListaVacia()
    {
        var ayer = new DateOnly(2026, 10, 1);
        var existentes = Enumerable.Range(0, 14)
            .Select(i => ConSoloFecha(ayer.AddDays(-i)))
            .ToList();

        var resultado = InformeDiario.CalcularFechasAProcesar(existentes, fechaManual: null, ayer, ventanaDias: 14, tope: 5);

        Assert.Empty(resultado);
    }

    // 7. Hueco intermedio (pedido explícito del usuario): existen 25, 26, 28
    // dentro de la ventana -- max(fecha)+1..ayer nunca habría detectado esto
    // porque 28 ya es más reciente que cualquier "última fecha" calculada
    // así. Recupera exactamente [27].
    [Fact]
    public void CalcularFechasAProcesar_HuecoIntermedio_RecuperaSoloLaFechaFaltante()
    {
        var dia25 = new DateOnly(2026, 9, 25);
        var dia26 = new DateOnly(2026, 9, 26);
        var dia27 = new DateOnly(2026, 9, 27);
        var dia28 = new DateOnly(2026, 9, 28);
        var existentes = new List<InformeDiario> { ConSoloFecha(dia25), ConSoloFecha(dia26), ConSoloFecha(dia28) };

        var resultado = InformeDiario.CalcularFechasAProcesar(existentes, fechaManual: null, ayer: dia28, ventanaDias: 4, tope: 5);

        Assert.Equal(new[] { dia27 }, resultado);
    }

    // 8. Hueco de 10 días con tope=5: las 5 MÁS RECIENTES, en orden
    // descendente (ayer, ayer-1, ayer-2, ayer-3, ayer-4) -- ajuste del
    // usuario sobre el primer borrador (que tomaba las más antiguas): tras
    // una caída larga lo reciente vale más, y lo viejo que siga realmente
    // abierto lo termina cubriendo el barrido `activas`.
    [Fact]
    public void CalcularFechasAProcesar_HuecoDe10DiasConTope5_DevuelveLasMasRecientesDescendente()
    {
        var ayer = new DateOnly(2026, 10, 1);
        // Ventana de 14 días con solo los 4 más antiguos ya existentes --
        // deja un hueco de 10 días (ayer..ayer-9) dentro de la ventana.
        var existentes = Enumerable.Range(10, 4)
            .Select(i => ConSoloFecha(ayer.AddDays(-i)))
            .ToList();

        var resultado = InformeDiario.CalcularFechasAProcesar(existentes, fechaManual: null, ayer, ventanaDias: 14, tope: 5);

        Assert.Equal(
            new[] { ayer, ayer.AddDays(-1), ayer.AddDays(-2), ayer.AddDays(-3), ayer.AddDays(-4) },
            resultado);
    }

    // 9. --fecha manual siempre gana: ignora la ventana y el hueco por completo.
    [Fact]
    public void CalcularFechasAProcesar_ConFechaManual_IgnoraElHueco()
    {
        var ayer = new DateOnly(2026, 10, 1);
        var fechaManual = new DateOnly(2026, 8, 1);

        var resultado = InformeDiario.CalcularFechasAProcesar(
            informesExistentes: new List<InformeDiario>(), fechaManual, ayer, ventanaDias: 14, tope: 5);

        Assert.Equal(new[] { fechaManual }, resultado);
    }
}
