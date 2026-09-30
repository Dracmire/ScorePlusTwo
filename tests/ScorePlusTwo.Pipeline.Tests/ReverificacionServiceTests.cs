using ScorePlusTwo.Pipeline.Api;
using ScorePlusTwo.Pipeline.Modelos;
using ScorePlusTwo.Pipeline.Verificacion;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

// Tests unitarios obligatorios del follow-up "candidatas con fecha_cierre/
// estado desactualizados" (2026-09-30, ver Verificacion/
// ReverificacionService.cs) — lógica pura o testeable sin red: el paso
// automático corre sin supervisión, así que estos 8 casos son la única
// red de seguridad real.
public class ReverificacionServiceTests
{
    private static Candidata Candidata(
        string codigo, DateTime? fechaCierre = null, DateTime? ultimaVerificacion = null) => new()
    {
        Codigo = codigo,
        Nombre = "Prueba " + codigo,
        Tipo = "LE",
        FechaCierre = fechaCierre,
        UltimaVerificacion = ultimaVerificacion,
    };

    private static DetalleLicitacion Detalle(string codigo, int codigoEstado, DateTime? fechaCierre = null) => new(
        CodigoExterno: codigo,
        Items: null,
        Comprador: null,
        Moneda: null,
        VisibilidadMonto: null,
        MontoEstimado: null,
        CantidadReclamos: null,
        CodigoEstado: codigoEstado,
        Fechas: fechaCierre is null ? null : new DetalleFechas(fechaCierre));

    // 1. ClasificarEstadoApi: 5 -> SiguePublicada; 19 -> Suspendida;
    // 6/7/8 -> EsTerminal con el EstadoFlujo correspondiente; 18 ->
    // EsTerminal/Revocada; un código no documentado (15, visto en el
    // fixture) -> EsTerminal/Cerrada (fallback conservador).
    [Fact]
    public void ClasificarEstadoApi_MapeaLosTresResultadosSegunCodigoEstado()
    {
        Assert.Equal(
            (ReverificacionService.ResultadoEstadoApi.SiguePublicada, (EstadoFlujo?)null),
            ReverificacionService.ClasificarEstadoApi(5));
        Assert.Equal(
            (ReverificacionService.ResultadoEstadoApi.Suspendida, (EstadoFlujo?)null),
            ReverificacionService.ClasificarEstadoApi(19));
        Assert.Equal(
            (ReverificacionService.ResultadoEstadoApi.EsTerminal, (EstadoFlujo?)EstadoFlujo.Cerrada),
            ReverificacionService.ClasificarEstadoApi(6));
        Assert.Equal(
            (ReverificacionService.ResultadoEstadoApi.EsTerminal, (EstadoFlujo?)EstadoFlujo.Desierta),
            ReverificacionService.ClasificarEstadoApi(7));
        Assert.Equal(
            (ReverificacionService.ResultadoEstadoApi.EsTerminal, (EstadoFlujo?)EstadoFlujo.Adjudicada),
            ReverificacionService.ClasificarEstadoApi(8));
        Assert.Equal(
            (ReverificacionService.ResultadoEstadoApi.EsTerminal, (EstadoFlujo?)EstadoFlujo.Revocada),
            ReverificacionService.ClasificarEstadoApi(18));
        Assert.Equal(
            (ReverificacionService.ResultadoEstadoApi.EsTerminal, (EstadoFlujo?)EstadoFlujo.Cerrada),
            ReverificacionService.ClasificarEstadoApi(15));
    }

    // 2. SeleccionarTope: (a) una candidata nunca verificada queda antes
    // que una verificada ayer aunque su FechaCierre sea más reciente; (b)
    // una candidata con FechaCierre == null es elegible y entra al
    // resultado; (c) el tope sigue siendo combinado entre ambas listas.
    [Fact]
    public void SeleccionarTope_OrdenaPorUltimaVerificacionYFechaCierre_ConTopeCombinado()
    {
        var ahora = new DateTime(2026, 9, 30, 3, 0, 0);

        var nuncaVerificadaFechaCercana = Candidata("nunca-cercana", fechaCierre: ahora.AddDays(-1));
        var nuncaVerificadaSinFecha = Candidata("nunca-sin-fecha", fechaCierre: null);
        var verificadaAyerFechaMuyVencida = Candidata(
            "verificada-ayer", fechaCierre: ahora.AddDays(-30), ultimaVerificacion: ahora.AddDays(-1));
        var verificadaHoy = Candidata("verificada-hoy", fechaCierre: ahora.AddDays(-1), ultimaVerificacion: ahora);
        var triaged = Candidata("triaged", fechaCierre: ahora.AddDays(-1));
        triaged.EstadoFlujo = EstadoFlujo.Candidata;
        var fechaFutura = Candidata("futura", fechaCierre: ahora.AddDays(5));

        var secundarias = new List<Candidata> { verificadaAyerFechaMuyVencida, triaged, verificadaHoy };
        var tramoBajo = new List<Candidata> { nuncaVerificadaFechaCercana, nuncaVerificadaSinFecha, fechaFutura };

        var sinTope = ReverificacionService.SeleccionarTope(secundarias, tramoBajo, ahora, tope: 100);

        // (a) nunca verificada antes que verificada, sin importar la fecha de cierre.
        Assert.Equal(
            new[] { "nunca-cercana", "nunca-sin-fecha", "verificada-ayer" },
            sinTope.Select(c => c.Codigo));

        // (b) FechaCierre == null es elegible.
        Assert.Contains(sinTope, c => c.Codigo == "nunca-sin-fecha");

        // No pendiente, ya verificada hoy, o fecha futura: quedan fuera.
        Assert.DoesNotContain(sinTope, c => c.Codigo is "triaged" or "verificada-hoy" or "futura");

        // (c) tope combinado entre ambas listas, no N de cada una por separado.
        var conTope = ReverificacionService.SeleccionarTope(secundarias, tramoBajo, ahora, tope: 2);
        Assert.Equal(new[] { "nunca-cercana", "nunca-sin-fecha" }, conTope.Select(c => c.Codigo));
    }

    // 3. No sobrescribir fecha con null: una candidata con FechaCierre
    // poblada, reverificada contra un detalle cuyo Fechas.FechaCierre es
    // null, debe conservar la FechaCierre original sin cambios.
    [Fact]
    public async Task ReverificarCandidatasAsync_NoSobrescribeFechaConNull()
    {
        var fechaOriginal = new DateTime(2026, 10, 1);
        var candidata = Candidata("1-1-LE26", fechaCierre: fechaOriginal);

        Task<DetalleLicitacion?> ObtenerDetalle(string codigo, CancellationToken ct) =>
            Task.FromResult<DetalleLicitacion?>(Detalle(codigo, codigoEstado: 5, fechaCierre: null));

        var resultado = await ReverificacionService.ReverificarCandidatasAsync(
            ObtenerDetalle, new List<Candidata> { candidata }, new HashSet<EstadoFlujo>(), DateTime.UtcNow);

        Assert.Equal(fechaOriginal, candidata.FechaCierre);
        Assert.Equal(0, resultado.CambiosFecha);
    }

    // 4. Fallo blando aislado: con el delegate de prueba que falla para un
    // código del medio de la lista, los demás sí se verifican
    // (UltimaVerificacion actualizada) y el fallido queda intacto, contado
    // en Fallidas, sin abortar el resto del loop.
    [Fact]
    public async Task ReverificarCandidatasAsync_FalloDeRedEnUnaCandidata_AislaElFalloSinAbortarElResto()
    {
        var ok1 = Candidata("ok-1");
        var falla = Candidata("falla-1");
        var ok2 = Candidata("ok-2");
        var ahora = new DateTime(2026, 9, 30);

        Task<DetalleLicitacion?> ObtenerDetalle(string codigo, CancellationToken ct)
        {
            if (codigo == "falla-1")
            {
                throw new MercadoPublicoApiException("simulado: agotados los reintentos");
            }

            return Task.FromResult<DetalleLicitacion?>(Detalle(codigo, codigoEstado: 5));
        }

        var resultado = await ReverificacionService.ReverificarCandidatasAsync(
            ObtenerDetalle, new List<Candidata> { ok1, falla, ok2 }, new HashSet<EstadoFlujo>(), ahora);

        Assert.Equal(1, resultado.Fallidas);
        Assert.Equal(2, resultado.Verificadas);
        Assert.Equal(ahora, ok1.UltimaVerificacion);
        Assert.Equal(ahora, ok2.UltimaVerificacion);
        Assert.Null(falla.UltimaVerificacion);
        Assert.Equal(0, falla.IntentosNoEncontrada);
    }

    // 5. IntentosNoEncontrada — 3 intentos y reset: una candidata cuya
    // respuesta simulada no trae el código dos veces seguidas sube a 1,
    // luego a 2 (EstadoFlujo sin cambios), y a la tercera pasa a terminal
    // Cerrada/MotivoCierre="no_encontrada_en_api"; un segundo caso donde,
    // tras una miss, el código SÍ se encuentra, confirma que
    // IntentosNoEncontrada vuelve a 0, MotivoCierre vuelve a null y
    // EstadoMp se puebla con el CodigoEstado real.
    [Fact]
    public async Task ReverificarCandidatasAsync_IntentosNoEncontrada_TresIntentosATerminalYResetAlEncontrarse()
    {
        var candidataQueDesaparece = Candidata("desaparece-1");
        var dias = new[] { new DateTime(2026, 9, 28), new DateTime(2026, 9, 29), new DateTime(2026, 9, 30) };

        Task<DetalleLicitacion?> NoEncontrada(string codigo, CancellationToken ct) =>
            Task.FromResult<DetalleLicitacion?>(null);

        var r1 = await ReverificacionService.ReverificarCandidatasAsync(
            NoEncontrada, new List<Candidata> { candidataQueDesaparece }, new HashSet<EstadoFlujo>(), dias[0]);
        Assert.Equal(1, candidataQueDesaparece.IntentosNoEncontrada);
        Assert.Equal(EstadoFlujo.Pendiente, candidataQueDesaparece.EstadoFlujo);
        Assert.Empty(r1.PasaronATerminal);

        var r2 = await ReverificacionService.ReverificarCandidatasAsync(
            NoEncontrada, new List<Candidata> { candidataQueDesaparece }, new HashSet<EstadoFlujo>(), dias[1]);
        Assert.Equal(2, candidataQueDesaparece.IntentosNoEncontrada);
        Assert.Equal(EstadoFlujo.Pendiente, candidataQueDesaparece.EstadoFlujo);
        Assert.Empty(r2.PasaronATerminal);

        var r3 = await ReverificacionService.ReverificarCandidatasAsync(
            NoEncontrada, new List<Candidata> { candidataQueDesaparece }, new HashSet<EstadoFlujo>(), dias[2]);
        Assert.Equal(3, candidataQueDesaparece.IntentosNoEncontrada);
        Assert.Equal(EstadoFlujo.Cerrada, candidataQueDesaparece.EstadoFlujo);
        Assert.Equal("no_encontrada_en_api", candidataQueDesaparece.MotivoCierre);
        Assert.Single(r3.PasaronATerminal);

        // Segundo caso: tras una miss, el código SÍ se encuentra.
        var candidataQueReaparece = Candidata("reaparece-1");
        await ReverificacionService.ReverificarCandidatasAsync(
            NoEncontrada, new List<Candidata> { candidataQueReaparece }, new HashSet<EstadoFlujo>(), dias[0]);
        Assert.Equal(1, candidataQueReaparece.IntentosNoEncontrada);

        Task<DetalleLicitacion?> Encontrada(string codigo, CancellationToken ct) =>
            Task.FromResult<DetalleLicitacion?>(Detalle(codigo, codigoEstado: 5));

        await ReverificacionService.ReverificarCandidatasAsync(
            Encontrada, new List<Candidata> { candidataQueReaparece }, new HashSet<EstadoFlujo>(), dias[1]);

        Assert.Equal(0, candidataQueReaparece.IntentosNoEncontrada);
        Assert.Null(candidataQueReaparece.MotivoCierre);
        Assert.Equal(5, candidataQueReaparece.EstadoMp);
    }

    // 6. FusionarLista no resucita cierres confirmados, pero sí permite
    // reingresar un cierre inferido — probado directamente sobre
    // FiltroLicitaciones.EsCodigoNuevo, la función pura extraída de
    // FusionarLista (Program.cs, private, no testeable directamente sin
    // InternalsVisibleTo). Caso (a): 5482-100-LP26 con cierre confirmado
    // (MotivoCierre null) en histórico, ausente de la lista activa, con un
    // override vigente a Prioritarias — NO se resucita. Caso (b): mismo
    // escenario pero MotivoCierre == "no_encontrada_en_api" — SÍ se
    // permite reingresar.
    [Fact]
    public void EsCodigoNuevo_NoResucitaCierreConfirmado_PeroPermiteReingresoDeCierreInferido()
    {
        var codigosActivos = new HashSet<string>(); // 5482-100-LP26 ausente de toda lista activa
        var motivoConfirmado = new Dictionary<string, string?> { ["5482-100-LP26"] = null };
        var motivoInferido = new Dictionary<string, string?> { ["5482-100-LP26"] = "no_encontrada_en_api" };

        Assert.False(Filtro.FiltroLicitaciones.EsCodigoNuevo("5482-100-LP26", codigosActivos, motivoConfirmado));
        Assert.True(Filtro.FiltroLicitaciones.EsCodigoNuevo("5482-100-LP26", codigosActivos, motivoInferido));

        // Un código ya en la lista activa nunca es nuevo, sea cual sea su histórico.
        var yaActivo = new HashSet<string> { "1-1-LE26" };
        Assert.False(Filtro.FiltroLicitaciones.EsCodigoNuevo("1-1-LE26", yaActivo, new Dictionary<string, string?>()));

        // Un código sin ningún registro en histórico es nuevo, como siempre.
        Assert.True(Filtro.FiltroLicitaciones.EsCodigoNuevo("nunca-visto", codigosActivos, motivoConfirmado));
    }

    // 7. Fuente de la fecha — caso trampa: un detalle sintético con
    // Fechas.FechaCierre poblada debe actualizar la FechaCierre de la
    // candidata con ese valor (confirma que ReverificarCandidatasAsync lee,
    // a través de EnriquecimientoUnspscService.ConstruirEntrada, el campo
    // anidado correcto — el modelo DetalleLicitacion ni siquiera declara un
    // FechaCierre de raíz, precisamente porque la API real lo devuelve
    // siempre null y confirmó no tener ningún uso, ver 598-16-LE26).
    [Fact]
    public async Task ReverificarCandidatasAsync_ActualizaFechaCierreDesdeFechasAnidada()
    {
        var candidata = Candidata("598-16-LE26", fechaCierre: null);
        var nuevaFecha = new DateTime(2026, 10, 15);

        Task<DetalleLicitacion?> ObtenerDetalle(string codigo, CancellationToken ct) =>
            Task.FromResult<DetalleLicitacion?>(Detalle(codigo, codigoEstado: 5, fechaCierre: nuevaFecha));

        var resultado = await ReverificacionService.ReverificarCandidatasAsync(
            ObtenerDetalle, new List<Candidata> { candidata }, new HashSet<EstadoFlujo>(), DateTime.UtcNow);

        Assert.Equal(nuevaFecha, candidata.FechaCierre);
        Assert.Equal(1, resultado.CambiosFecha);
    }

    // 8. Eventos solo en la transición: dos corridas consecutivas donde la
    // API devuelve el mismo CodigoEstado terminal (7, Desierta) para una
    // candidata triaged deben generar UN SOLO evento
    // cierre_detectado_en_triage y un solo incremento de
    // CierresDetectadosEnTriage — no dos.
    [Fact]
    public async Task ReverificarCandidatasAsync_EmiteCierreDetectadoEnTriageSoloEnLaTransicion()
    {
        var candidataTriaged = Candidata("triaged-1");
        candidataTriaged.EstadoFlujo = EstadoFlujo.Scorecard;
        var estadosTriage = new HashSet<EstadoFlujo> { EstadoFlujo.Candidata, EstadoFlujo.Scorecard, EstadoFlujo.Enviada };

        Task<DetalleLicitacion?> Desierta(string codigo, CancellationToken ct) =>
            Task.FromResult<DetalleLicitacion?>(Detalle(codigo, codigoEstado: 7));

        var r1 = await ReverificacionService.ReverificarCandidatasAsync(
            Desierta, new List<Candidata> { candidataTriaged }, estadosTriage, new DateTime(2026, 9, 29));
        Assert.Equal(1, r1.CierresDetectadosEnTriage);
        Assert.Single(r1.Eventos, e => e.Accion == "cierre_detectado_en_triage");
        // EstadoFlujo NUNCA cambia para triaged, aunque el resultado sea terminal.
        Assert.Equal(EstadoFlujo.Scorecard, candidataTriaged.EstadoFlujo);

        var r2 = await ReverificacionService.ReverificarCandidatasAsync(
            Desierta, new List<Candidata> { candidataTriaged }, estadosTriage, new DateTime(2026, 9, 30));
        Assert.Equal(0, r2.CierresDetectadosEnTriage);
        Assert.Empty(r2.Eventos);
        Assert.Equal(EstadoFlujo.Scorecard, candidataTriaged.EstadoFlujo);
    }
}
