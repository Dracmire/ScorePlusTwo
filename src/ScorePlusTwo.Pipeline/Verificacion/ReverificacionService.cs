using ScorePlusTwo.Pipeline;
using ScorePlusTwo.Pipeline.Api;
using ScorePlusTwo.Pipeline.Enriquecimiento;
using ScorePlusTwo.Pipeline.Modelos;

namespace ScorePlusTwo.Pipeline.Verificacion;

// Por qué existe (2026-09-30): ni FusionarLista (el dedupe — solo agrega
// códigos nuevos, nunca vuelve a tocar uno existente) ni RevalidarEstado
// (solo cruza contra el lote diario CRUDO, que solo trae códigos con
// "movimiento" ese día) pueden detectar que una candidata Pendiente/
// triaged se cerró o cambió de fecha si el código deja de aparecer en el
// feed incremental — exactamente lo que le pasó a 591-26-LP26/
// 5482-103-LP26/5482-100-LP26 (ver el follow-up que motivó este archivo).
// Este servicio SÍ hace una llamada de detalle real por candidata, la
// única forma de enterarse sin depender de que el código reaparezca solo.
//
// Impuro (ReverificarCandidatasAsync hace I/O vía el delegate que recibe),
// mismo principio de asimetría que EnriquecimientoUnspscService: un fallo
// puntual (reintentos agotados) nunca es fatal para el resto del lote.
public static class ReverificacionService
{
    public enum ResultadoEstadoApi
    {
        SiguePublicada,
        Suspendida,
        EsTerminal,
    }

    // PasaronATerminal: candidatas Pendiente que llegaron a un EstadoFlujo
    // terminal en esta corrida (EstadoFlujo YA actualizado) — el llamador
    // decide a qué histórico moverlas y las remueve de la lista activa. Las
    // "triaged degradadas in-place" (evento cierre_detectado_en_triage)
    // NUNCA entran acá: por diseño no cambian EstadoFlujo ni se mueven de
    // lista, solo quedan con FechaCierre/UltimaVerificacion refrescados.
    //
    // PublicadaPeroVencida (trailing, con default): cuántas candidatas
    // devolvieron SiguePublicada con una FechaCierre (recién actualizada o
    // la ya almacenada) que ya pasó — evidencia real de licitaciones que
    // Mercado Público sigue mostrando "Publicada" pese a estar vencidas en
    // el calendario, para reportar en la primera corrida del modo manual
    // sin tener que adivinar. Al final y con default para no romper ningún
    // constructor posicional existente con los 6 campos base.
    public sealed record ResultadoReverificacion(
        int Verificadas,
        int CambiosFecha,
        List<Candidata> PasaronATerminal,
        int CierresDetectadosEnTriage,
        int Fallidas,
        List<EventoAuditoria> Eventos,
        int PublicadaPeroVencida = 0);

    // Pura, testeable sin red. Reusa MapearEstadoDeCierre (promovida de
    // private a internal en Program.cs, mismo criterio ya usado con
    // EvaluarRubro/EsRegionElegible) para 6/7/8 — es la única función que
    // decide el tratamiento de 5/15/16/18/19/no documentado. Las dos ramas
    // de Program.EjecutarReevaluarInventarioAsync la reusan en vez de
    // mapear por su cuenta (2026-09-30, hotfix). RevalidarEstado
    // (Program.cs, sobre el lote diario crudo) sigue usando
    // MapearEstadoDeCierre directamente, sin pasar por acá — pendiente
    // conocido, sin riesgo de cierre erróneo (MapearEstadoDeCierre nunca
    // mapea 15/16/18/19), solo un posible retraso de una corrida hasta
    // que la re-verificación automática lo alcance.
    //
    // Valores reales de CodigoEstado conocidos: 5 Publicada (no terminal),
    // 15 Revocada (terminal), 16 Suspendida (NO terminal — puede
    // reactivarse), 18/19 se conservan con el mismo tratamiento (Revocada/
    // Suspendida respectivamente) por si el listado los usa en otro
    // contexto, 6/7/8 Cerrada/Desierta/Adjudicada (terminal, ya mapeados),
    // cualquier otro valor no documentado — mismo fallback conservador que
    // --reevaluar-inventario ya usa: Cerrada + advertencia impresa, nunca
    // silencioso.
    //
    // 15/16 verificados a mano contra Mercado Público el 2026-09-30
    // (usuario), tras la primera corrida real de re-verificación:
    // 1007793-15-LE26 (6) = Cerrada [correcto]; 732-14-LP26 y
    // 5482-100-LP26 (15) = Revocada, NO Cerrada; 2369-70-LR26 (16) =
    // Suspendida, NO terminal. El fallback anterior trataba 15/16 como no
    // documentados y los cerraba — cerró una Revocada (destino correcto,
    // EstadoFlujo equivocado) y, más grave, cerró y bloqueó para
    // reingreso una Suspendida que puede reactivarse.
    //
    // codigoExterno (opcional): solo para enriquecer el mensaje de
    // advertencia del fallback no documentado con qué código lo disparó —
    // no cambia la clasificación.
    public static (ResultadoEstadoApi Resultado, EstadoFlujo? EstadoTerminal) ClasificarEstadoApi(
        int codigoEstado, string? codigoExterno = null)
    {
        if (codigoEstado == 5)
        {
            return (ResultadoEstadoApi.SiguePublicada, null);
        }

        if (codigoEstado == 16 || codigoEstado == 19)
        {
            return (ResultadoEstadoApi.Suspendida, null);
        }

        if (codigoEstado == 15 || codigoEstado == 18)
        {
            return (ResultadoEstadoApi.EsTerminal, EstadoFlujo.Revocada);
        }

        if (Program.MapearEstadoDeCierre(codigoEstado) is { } estadoCierre)
        {
            return (ResultadoEstadoApi.EsTerminal, estadoCierre);
        }

        Console.Error.WriteLine(
            $"[ADVERTENCIA] CodigoEstado no documentado ({codigoEstado})" +
            (codigoExterno is null ? "" : $" en {codigoExterno}") +
            " en reverificación, tratado como Cerrada por defecto.");
        return (ResultadoEstadoApi.EsTerminal, EstadoFlujo.Cerrada);
    }

    // Pura, testeable. Elegibles: Pendiente && (FechaCierre is null ||
    // FechaCierre < ahora) && (UltimaVerificacion is null ||
    // UltimaVerificacion.Value.Date != ahora.Date) — una candidata con
    // FechaCierre == null (nunca se pudo determinar, o nunca se enriqueció)
    // también es elegible, no solo las "vencidas" con fecha conocida, o
    // nunca se re-verificaría. Orden: primero por UltimaVerificacion
    // ascendente con null PRIMERO (las nunca verificadas van siempre antes
    // que cualquier verificada, sin importar qué tan reciente sea su
    // FechaCierre — el comparador de Nullable<DateTime> ya ordena null
    // como el menor valor posible en ascendente); como desempate, por
    // FechaCierre ascendente con null AL FINAL del grupo (por eso se
    // sustituye por DateTime.MaxValue en la clave de orden, en vez de
    // dejar que el comparador por defecto lo ponga primero). Take(tope)
    // sobre el conjunto ya ordenado y combinado — no N de cada lista por
    // separado.
    public static IReadOnlyList<Candidata> SeleccionarTope(
        IReadOnlyList<Candidata> secundarias, IReadOnlyList<Candidata> tramoBajo, DateTime ahora, int tope) =>
        SeleccionarElegibles(secundarias, tramoBajo, ahora, omitirVerificadasHoy: true)
            .Take(tope)
            .ToList();

    // Usado por el modo manual --reverificar-vencidas (Program.
    // EjecutarReverificarVencidasAsync): mismo criterio de elegibilidad y
    // orden que SeleccionarTope, pero sin tope (backlog completo de una
    // sola vez) y sin el salto de "ya verificada hoy" — correr este modo
    // dos veces el mismo día debe seguir procesando lo que quede, a
    // diferencia del paso diario, que es incremental por diseño.
    public static IReadOnlyList<Candidata> SeleccionarVencidasSinTope(
        IReadOnlyList<Candidata> secundarias, IReadOnlyList<Candidata> tramoBajo, DateTime ahora) =>
        SeleccionarElegibles(secundarias, tramoBajo, ahora, omitirVerificadasHoy: false)
            .ToList();

    private static IEnumerable<Candidata> SeleccionarElegibles(
        IReadOnlyList<Candidata> secundarias, IReadOnlyList<Candidata> tramoBajo, DateTime ahora, bool omitirVerificadasHoy)
    {
        bool EsElegible(Candidata c) =>
            c.EstadoFlujo == EstadoFlujo.Pendiente
            && (c.FechaCierre is null || c.FechaCierre < ahora)
            && (!omitirVerificadasHoy || c.UltimaVerificacion is null || c.UltimaVerificacion.Value.Date != ahora.Date);

        return secundarias.Concat(tramoBajo)
            .Where(EsElegible)
            .OrderBy(c => c.UltimaVerificacion)
            .ThenBy(c => c.FechaCierre ?? DateTime.MaxValue);
    }

    // Impura: obtenerDetalle hace la llamada de red real (inyectado como
    // delegate, no como MercadoPublicoClient concreto, para poder testear
    // el fallo blando aislado y el ciclo de IntentosNoEncontrada sin
    // HttpClient real). Devuelve null cuando la API respondió bien pero el
    // código no vino en Listado — DEBE lanzar MercadoPublicoApiException
    // (no devolver null) cuando la llamada falla de verdad (red/reintentos
    // agotados): son dos fallos DISTINTOS, tratados distinto.
    //
    // estadosTriage: EstadoFlujo con triage humano encima para ESTA lista
    // (para Prioritarias, {Candidata, Scorecard, Enviada}; para
    // Secundarias/TramoBajo, vacío — SeleccionarTope/SeleccionarVencidasSinTope
    // solo seleccionan Pendiente, así que nunca hay triaged en esa lista).
    // Una candidata cuyo EstadoFlujo está en estadosTriage NUNCA cambia de
    // EstadoFlujo ni se mueve de lista, pase lo que pase en la API — el
    // triage humano no se pisa (mismo invariante ya aplicado en
    // RevalidarEstado/--reevaluar-inventario). Si el resultado es
    // EsTerminal y es la primera vez que se observa ese CodigoEstado (ver
    // más abajo), se emite cierre_detectado_en_triage en vez de mover de
    // lista.
    //
    // ahora: SIEMPRE Program.AhoraChile() en el llamador, nunca
    // DateTime.UtcNow — FechaCierre es hora de Chile SIN ZONA tal cual la
    // entrega la API, y la corrida real aterriza entre ~22:52 y ~01:58 CLT
    // (rango de atraso del cron ya documentado): comparar esa fecha contra
    // un "ahora" en UTC compararía naranjas con manzanas cerca de la
    // medianoche, el mismo tipo de error ya corregido una vez para el
    // cálculo de "ayer" del lote diario.
    public static async Task<ResultadoReverificacion> ReverificarCandidatasAsync(
        Func<string, CancellationToken, Task<DetalleLicitacion?>> obtenerDetalle,
        IReadOnlyList<Candidata> candidatas,
        IReadOnlySet<EstadoFlujo> estadosTriage,
        DateTime ahora,
        CancellationToken ct = default)
    {
        var pasaronATerminal = new List<Candidata>();
        var eventos = new List<EventoAuditoria>();
        var verificadas = 0;
        var cambiosFecha = 0;
        var cierresDetectadosEnTriage = 0;
        var fallidas = 0;
        var publicadaPeroVencida = 0;

        foreach (var candidata in candidatas)
        {
            DetalleLicitacion? detalle;
            try
            {
                detalle = await obtenerDetalle(candidata.Codigo, ct);
            }
            catch (MercadoPublicoApiException ex)
            {
                // Clase 1 — error de red/reintentos agotados: fallo blando,
                // la candidata queda intacta (UltimaVerificacion/
                // IntentosNoEncontrada SIN tocar), se reintenta sola la
                // próxima corrida. Nunca aborta el resto del lote.
                Console.Error.WriteLine(
                    $"[ADVERTENCIA] Reverificación omitida para {candidata.Codigo}, se reintenta la próxima corrida: {ex.Message}");
                fallidas++;
                continue;
            }

            verificadas++;

            if (detalle is null)
            {
                // Clase 2 — respuesta válida pero el código no vino en
                // Listado: DISTINTO de un error de red. Cuenta para el
                // ciclo de 3 intentos, pero SÍ actualiza UltimaVerificacion
                // (para no quedar para siempre a la cabeza de la cola de
                // SeleccionarTope por tener UltimaVerificacion en null).
                var intentosPrevios = candidata.IntentosNoEncontrada;
                candidata.IntentosNoEncontrada = Math.Min(intentosPrevios + 1, 3);
                candidata.UltimaVerificacion = ahora;

                // Transición exacta 2 -> 3, no una corrida posterior
                // mientras siga sin encontrarse (el contador ya está
                // cappeado en 3, así que esta comparación solo es
                // verdadera una vez).
                if (intentosPrevios == 2 && candidata.IntentosNoEncontrada == 3)
                {
                    candidata.MotivoCierre = "no_encontrada_en_api";
                    var esTriaged = estadosTriage.Contains(candidata.EstadoFlujo);

                    if (esTriaged)
                    {
                        cierresDetectadosEnTriage++;
                        eventos.Add(new EventoAuditoria(
                            DateTime.UtcNow, "sistema", "cierre_detectado_en_triage", candidata.Codigo,
                            $"estado_flujo_triage={candidata.EstadoFlujo} estado_terminal_detectado=cerrada motivo=no_encontrada_en_api"));
                    }
                    else
                    {
                        candidata.EstadoFlujo = EstadoFlujo.Cerrada;
                        pasaronATerminal.Add(candidata);
                        eventos.Add(new EventoAuditoria(
                            DateTime.UtcNow, "sistema", "estado_actualizado", candidata.Codigo,
                            "estado_nuevo=cerrada motivo=no_encontrada_en_api"));
                    }
                }

                continue;
            }

            // Clase 3 — encontrada: resetea el ciclo de "no encontrada" y
            // registra el CodigoEstado observado. estadoMpPrevio se
            // captura ANTES de sobrescribir EstadoMp — es lo que permite
            // comparar la transición más abajo (eventos solo cuando algo
            // cambia, no en cada corrida mientras la candidata se quede en
            // el mismo estado).
            var estadoMpPrevio = candidata.EstadoMp;
            var codigoEstado = detalle.CodigoEstado;
            candidata.IntentosNoEncontrada = 0;
            candidata.MotivoCierre = null;
            candidata.EstadoMp = codigoEstado;
            candidata.UltimaVerificacion = ahora;

            // Fuente de la fecha: Fechas.FechaCierre vía
            // EnriquecimientoUnspscService.ConstruirEntrada, NUNCA el
            // FechaCierre de la raíz del detalle (siempre null en la
            // práctica — trampa ya documentada contra 598-16-LE26). Se
            // reusa el mismo helper que --refrescar-descriptivos, en vez
            // de reescribir la extracción.
            var entradaEnriquecida = EnriquecimientoUnspscService.ConstruirEntrada(candidata.Codigo, detalle);
            var nuevaFecha = entradaEnriquecida.FechaCierre;

            // Nunca sobrescribe una fecha ya conocida con una ausente —
            // solo actualiza cuando vino poblada y es distinta de la ya
            // guardada.
            if (nuevaFecha is not null && nuevaFecha != candidata.FechaCierre)
            {
                var fechaAnterior = candidata.FechaCierre;
                candidata.FechaCierre = nuevaFecha;
                cambiosFecha++;
                eventos.Add(new EventoAuditoria(
                    DateTime.UtcNow, "sistema", "fecha_cierre_actualizada", candidata.Codigo,
                    $"fecha_anterior={FormatearFechaEvento(fechaAnterior)} fecha_nueva={FormatearFechaEvento(nuevaFecha)}"));
            }

            var huboTransicion = estadoMpPrevio != codigoEstado;
            var (resultado, estadoTerminal) = ClasificarEstadoApi(codigoEstado);
            var esTriagedEncontrada = estadosTriage.Contains(candidata.EstadoFlujo);

            switch (resultado)
            {
                case ResultadoEstadoApi.SiguePublicada:
                    if (candidata.FechaCierre is { } fechaCierreActual && fechaCierreActual < ahora)
                    {
                        publicadaPeroVencida++;
                    }

                    break;

                case ResultadoEstadoApi.Suspendida:
                    // Nunca cambia EstadoFlujo ni mueve de lista (una
                    // suspendida puede reactivarse) — solo el evento, y
                    // solo en la corrida de la transición.
                    if (huboTransicion)
                    {
                        eventos.Add(new EventoAuditoria(
                            DateTime.UtcNow, "sistema", "estado_actualizado", candidata.Codigo, "detalle=suspendida"));
                    }

                    break;

                case ResultadoEstadoApi.EsTerminal:
                    if (!esTriagedEncontrada)
                    {
                        candidata.EstadoFlujo = estadoTerminal!.Value;
                        pasaronATerminal.Add(candidata);
                        eventos.Add(new EventoAuditoria(
                            DateTime.UtcNow, "sistema", "estado_actualizado", candidata.Codigo,
                            $"estado_nuevo={estadoTerminal} codigo_estado_api={codigoEstado}"));
                    }
                    else if (huboTransicion)
                    {
                        cierresDetectadosEnTriage++;
                        eventos.Add(new EventoAuditoria(
                            DateTime.UtcNow, "sistema", "cierre_detectado_en_triage", candidata.Codigo,
                            $"estado_flujo_triage={candidata.EstadoFlujo} estado_terminal_detectado={estadoTerminal}"));
                    }

                    break;
            }
        }

        return new ResultadoReverificacion(
            verificadas, cambiosFecha, pasaronATerminal, cierresDetectadosEnTriage, fallidas, eventos, publicadaPeroVencida);
    }

    private static string FormatearFechaEvento(DateTime? fecha) =>
        fecha is null ? "null" : fecha.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
}
