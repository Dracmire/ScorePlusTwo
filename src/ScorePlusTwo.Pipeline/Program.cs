using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScorePlusTwo.Pipeline.Api;
using ScorePlusTwo.Pipeline.Cli;
using ScorePlusTwo.Pipeline.Dashboard;
using ScorePlusTwo.Pipeline.Enriquecimiento;
using ScorePlusTwo.Pipeline.Filtro;
using ScorePlusTwo.Pipeline.Infraestructura;
using ScorePlusTwo.Pipeline.Modelos;
using ScorePlusTwo.Pipeline.Persistencia;
using ScorePlusTwo.Pipeline.Refiltrado;
using ScorePlusTwo.Pipeline.Unspsc;

namespace ScorePlusTwo.Pipeline;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var opciones = OpcionesCli.Parse(args);
            var repoRoot = RutaRepo.Resolver();

            // Rama completamente aparte del flujo automático: --refiltrar
            // corre el filtro sobre TODO data/raw/ acumulado con un
            // criterios.json alternativo y escribe un CSV. La separación es
            // estructural (return antes de tocar cualquier otra variable del
            // flujo normal) para que sea imposible que este modo termine
            // escribiendo candidatas.json/informes.json/eventos.json/
            // docs/data.json por accidente.
            if (opciones.Refiltrar)
            {
                return EjecutarRefiltrado(repoRoot, opciones);
            }

            // Otra rama completamente aparte (mismo criterio estructural que
            // --refiltrar): reclasifica de una sola vez las Prioritarias que
            // el filtro de acumulados congeló antes de que existiera UNSPSC
            // (F2, PR #28) — nunca vuelven a pasar por enriquecimiento
            // mientras sigan confirmadas. Ver EjecutarBackfillUnspscAsync.
            if (opciones.BackfillUnspsc)
            {
                return await EjecutarBackfillUnspscAsync(repoRoot);
            }

            // Otra rama completamente aparte (2026-09-21, mismo criterio
            // estructural que --backfill-unspsc): mantenimiento puntual
            // sobre Prioritarias + Tramo bajo, revalidando Estado y
            // reclasificando con las reglas actuales (términos ambiguos
            // incluidos). Secundarias nunca se toca. Ver
            // EjecutarReevaluarInventarioAsync.
            if (opciones.ReevaluarInventario)
            {
                return await EjecutarReevaluarInventarioAsync(repoRoot);
            }

            // Otra rama completamente aparte (2026-09-21, mismo criterio
            // estructural que los dos anteriores): una sola llamada de
            // detalle sobre un código puntual, persistida en
            // data/consultas/{codigo}.json para que sirva de cache al botón
            // "Consultar" del tablero. Ver EjecutarConsultarLicitacionAsync.
            if (opciones.ConsultarLicitacion is not null)
            {
                return await EjecutarConsultarLicitacionAsync(repoRoot, opciones.ConsultarLicitacion);
            }

            // Otra rama completamente aparte (2026-09-22, mismo criterio
            // estructural que las tres anteriores): refresca campos
            // descriptivos de Prioritarias completas + la cola de Revisión
            // dentro de Secundarias — nunca reclasifica ni mueve de lista
            // (a propósito, para no confundirse con --reevaluar-inventario).
            // Ver EjecutarRefrescarDescriptivosAsync.
            if (opciones.RefrescarDescriptivos)
            {
                return await EjecutarRefrescarDescriptivosAsync(repoRoot);
            }

            // Otra rama completamente aparte (2026-09-30, mismo criterio
            // estructural que las cuatro anteriores): vacía de una sola vez
            // el backlog completo de candidatas Pendiente vencidas de
            // Secundarias + Tramo bajo contra el detalle real de la API,
            // sin el tope diario del paso automático. Nunca se dispara
            // automáticamente. Ver EjecutarReverificarVencidasAsync.
            if (opciones.ReverificarVencidas)
            {
                return await EjecutarReverificarVencidasAsync(repoRoot);
            }

            var criterios = JsonStore.Cargar<Criterios>(
                Path.Combine(repoRoot, "config", "criterios.json"), JsonOpciones.Config);
            var catalogoUnspsc = CatalogoUnspsc.CargarDesdeArchivo(
                Path.Combine(repoRoot, "config", "catalogo-unspsc.tsv"));

            // Cache de enriquecimiento UNSPSC (ver EntradaCacheUnspsc): un
            // hecho de negocio permanente, nunca se invalida. Se carga una
            // sola vez y se comparte entre el lote diario y el barrido
            // `activas` — si un mismo CodigoExterno aparece en ambos, la
            // segunda pasada ya lo encuentra cacheado y no repite la llamada.
            var rutaCacheUnspsc = Path.Combine(repoRoot, "data", "cache-unspsc.json");
            var cacheUnspscInicial = JsonStore.CargarOPredeterminado(
                rutaCacheUnspsc, JsonOpciones.Persistencia, new List<EntradaCacheUnspsc>());
            var cacheUnspsc = cacheUnspscInicial.ToDictionary(e => e.CodigoExterno);
            var cacheUnspscCountInicial = cacheUnspsc.Count;

            // Filtro de acumulados (F2, 2026-09-17): un CodigoExterno que ya
            // es una Prioritaria confirmada no se enriquece ni se reclasifica
            // de nuevo cada corrida — ya sabemos la respuesta. Excepción: si
            // se marca manualmente como falso positivo (EstadoFlujo.
            // Descartada), vuelve a entrar al flujo normal. Distinto del
            // cache: el cache evita la llamada de red, esto evita gastar el
            // presupuesto de enriquecimiento/clasificación en algo cuyo
            // destino ya está decidido — el propósito real es liberar ese
            // presupuesto para poder achicar descarte_duro más adelante.
            var candidatasExistentes = JsonStore.CargarOPredeterminado(
                Path.Combine(repoRoot, "data", "candidatas.json"), JsonOpciones.Persistencia, new List<Candidata>());
            var codigosConfirmados = candidatasExistentes
                .Where(c => c.EstadoFlujo != EstadoFlujo.Descartada)
                .Select(c => c.Codigo)
                .ToHashSet();

            if (opciones.RutaFixture is not null)
            {
                // Modo local: sin red, sin MP_TICKET, sin barrido activas, sin
                // enriquecimiento nuevo, sin recupero de fechas (un solo
                // fixture = un solo día) — clasifica con lo que ya haya en el
                // cache de disco (típicamente nada, en una copia fresca).
                var fecha = opciones.Fecha ?? DateOnly.FromDateTime(AhoraChile()).AddDays(-1);
                var rutaFixture = Path.IsPathRooted(opciones.RutaFixture)
                    ? opciones.RutaFixture
                    : Path.Combine(Directory.GetCurrentDirectory(), opciones.RutaFixture);

                var respuestaFixture = JsonStore.Cargar<ListadoLicitacionesResponse>(rutaFixture, JsonOpciones.ApiLectura);
                var loteFixture = respuestaFixture.Listado;
                GuardarRawDelDia(repoRoot, fecha, respuestaFixture);

                var (resultadoDiario, enriquecidosDiarioHoy, enriquecimientosFallidosDiarioHoy, ahorradosPorAcumuladosDiario) =
                    await FiltrarConEnriquecimientoAsync(loteFixture, criterios, cacheUnspsc, catalogoUnspsc, codigosConfirmados, cliente: null);

                if (cacheUnspsc.Count != cacheUnspscCountInicial)
                {
                    var cacheOrdenado = cacheUnspsc.Values.OrderBy(e => e.CodigoExterno, StringComparer.Ordinal).ToList();
                    JsonStore.Guardar(rutaCacheUnspsc, cacheOrdenado, JsonOpciones.Persistencia);
                }

                var tiposPrivadosFixture = criterios.TiposPrivados.ToHashSet();
                var rutaPrioritariasFixture = Path.Combine(repoRoot, "data", "candidatas.json");
                var rutaSecundariasFixture = Path.Combine(repoRoot, "data", "secundarias.json");
                var rutaTramoBajoFixture = Path.Combine(repoRoot, "data", "tramo_bajo.json");
                var rutaHistoricoPrioritariasFixture = Path.Combine(repoRoot, "data", "historico", "candidatas.json");
                var rutaHistoricoSecundariasFixture = Path.Combine(repoRoot, "data", "historico", "secundarias.json");
                var rutaHistoricoTramoBajoFixture = Path.Combine(repoRoot, "data", "historico", "tramo_bajo.json");

                var (todasPrioritariasSinOverride, nuevasPrioritariasDiario, _) = FusionarLista(
                    rutaPrioritariasFixture, resultadoDiario.Prioritarias, null, fecha, fecha, tramo: null,
                    tiposPrivadosFixture, CargarMotivoCierreHistorico(rutaHistoricoPrioritariasFixture));
                var (todasSecundariasSinOverride, nuevasSecundariasDiario, _) = FusionarLista(
                    rutaSecundariasFixture, resultadoDiario.Secundarias, null, fecha, fecha, tramo: null,
                    tiposPrivadosFixture, CargarMotivoCierreHistorico(rutaHistoricoSecundariasFixture));
                var (todasTramoBajo, nuevasTramoBajoDiario, _) = FusionarLista(
                    rutaTramoBajoFixture, resultadoDiario.TramoBajo, null, fecha, fecha, tramo: "bajo",
                    tiposPrivadosFixture, CargarMotivoCierreHistorico(rutaHistoricoTramoBajoFixture));

                var overridesFixture = JsonStore.CargarOPredeterminado(
                    Path.Combine(repoRoot, "data", "overrides.json"), JsonOpciones.Persistencia, new Dictionary<string, EntradaOverride>());
                var (todasPrioritarias, todasSecundarias, _) = AplicadorOverrides.Aplicar(
                    overridesFixture, todasPrioritariasSinOverride, todasSecundariasSinOverride);

                JsonStore.Guardar(rutaPrioritariasFixture, todasPrioritarias, JsonOpciones.Persistencia);
                JsonStore.Guardar(rutaSecundariasFixture, todasSecundarias, JsonOpciones.Persistencia);
                JsonStore.Guardar(rutaTramoBajoFixture, todasTramoBajo, JsonOpciones.Persistencia);

                var (prioritariasActivasFixture, prioritariasMovidas) = RevalidarEstado(
                    rutaPrioritariasFixture, rutaHistoricoPrioritariasFixture, todasPrioritarias, loteFixture);
                var (secundariasActivasFixture, secundariasMovidas) = RevalidarEstado(
                    rutaSecundariasFixture, rutaHistoricoSecundariasFixture, todasSecundarias, loteFixture);
                var (tramoBajoActivasFixture, tramoBajoMovidas) = RevalidarEstado(
                    rutaTramoBajoFixture, rutaHistoricoTramoBajoFixture, todasTramoBajo, loteFixture);
                var totalMovidasHistoricoFixture = prioritariasMovidas + secundariasMovidas + tramoBajoMovidas;

                Console.WriteLine("[reverificacion] omitida en modo --fixture (requiere MP_TICKET).");

                var informeHoy = new InformeDiario(
                    Fecha: fecha,
                    Total: resultadoDiario.Total, TrasEstado: resultadoDiario.TrasEstado, TrasTipo: resultadoDiario.TrasTipo,
                    TrasRegion: resultadoDiario.TrasRegion, DescarteDuro: resultadoDiario.DescarteDuro,
                    Prioritarias: resultadoDiario.Prioritarias.Count, Secundarias: resultadoDiario.Secundarias.Count,
                    TramoBajo: resultadoDiario.TramoBajo.Count,
                    NuevasPrioritarias: nuevasPrioritariasDiario.Count, NuevasSecundarias: nuevasSecundariasDiario.Count,
                    NuevasTramoBajo: nuevasTramoBajoDiario.Count,
                    BarridoActivas: null,
                    EnriquecidosHoy: enriquecidosDiarioHoy, EnriquecimientosFallidos: enriquecimientosFallidosDiarioHoy,
                    Bienes: resultadoDiario.Bienes, SinResolverUnspsc: resultadoDiario.SinResolverUnspsc,
                    RevisionManualUnspsc: resultadoDiario.RevisionManualUnspsc, AhorradosPorAcumulados: ahorradosPorAcumuladosDiario,
                    ReverificadasHoy: 0, ReverificacionesConCambioFecha: 0, ReverificacionesATerminal: 0,
                    ReverificacionesFallidas: 0, CierresDetectadosEnTriage: 0,
                    LlamadasEnriquecimiento: enriquecidosDiarioHoy + enriquecimientosFallidosDiarioHoy,
                    LlamadasReverificacion: 0, LlamadasBarridoActivas: 0);

                var informesFixture = ActualizarSerieInformes(repoRoot, informeHoy);
                JsonStore.Guardar(Path.Combine(repoRoot, "data", "informes.json"), informesFixture, JsonOpciones.Persistencia);

                var totalNuevasHoyFixture = nuevasPrioritariasDiario.Count + nuevasSecundariasDiario.Count + nuevasTramoBajoDiario.Count;
                RegistrarEvento(repoRoot, informeHoy, totalNuevasHoyFixture, totalMovidasHistoricoFixture);

                var dashboardFixture = GeneradorDashboard.Construir(
                    prioritariasActivasFixture, secundariasActivasFixture, tramoBajoActivasFixture, informesFixture, DateTime.UtcNow);
                JsonStore.Guardar(Path.Combine(repoRoot, "docs", "data.json"), dashboardFixture, JsonOpciones.Persistencia);
                PublicarPanelRevisionConfig(repoRoot);

                ImprimirResumenLoteDiario(
                    fecha, resultadoDiario, nuevasPrioritariasDiario.Count, nuevasSecundariasDiario.Count, nuevasTramoBajoDiario.Count,
                    enriquecidosDiarioHoy, enriquecimientosFallidosDiarioHoy, ahorradosPorAcumuladosDiario);
                ImprimirResumenActivas("omitido (modo --fixture, sin red)", null, 0, 0, 0);

                var llamadasFixture = enriquecidosDiarioHoy + enriquecimientosFallidosDiarioHoy;
                Console.WriteLine(
                    $"Cuota de llamadas de detalle: enriquecimiento={llamadasFixture} " +
                    $"reverificacion=0 barrido_activas=0 total={llamadasFixture}");

                return 0;
            }

            // Flujo real contra la API de Mercado Público (ticket requerido).
            {
                var ticket = Environment.GetEnvironmentVariable("MP_TICKET")
                    ?? throw new MercadoPublicoApiException("Falta la variable de entorno MP_TICKET.");

                // Un solo HttpClient/MercadoPublicoClient para TODA la
                // invocación (2026-10-01) — antes se creaba uno para el lote
                // diario+activas y OTRO aparte, descartable, solo para la
                // re-verificación (porque el primero vivía dentro del scope
                // de una sola fecha). Con el loop de recupero el cliente ya
                // tiene que sobrevivir a varias fechas de todas formas, así
                // que de paso se reutiliza también para activas y
                // re-verificación — mismo ticket, sin cambio de comportamiento,
                // una conexión menos por corrida.
                using var http = new HttpClient();
                var cliente = new MercadoPublicoClient(http, ticket);

                var tiposPrivados = criterios.TiposPrivados.ToHashSet();
                var rutaPrioritarias = Path.Combine(repoRoot, "data", "candidatas.json");
                var rutaSecundarias = Path.Combine(repoRoot, "data", "secundarias.json");
                var rutaTramoBajo = Path.Combine(repoRoot, "data", "tramo_bajo.json");
                var rutaHistoricoPrioritarias = Path.Combine(repoRoot, "data", "historico", "candidatas.json");
                var rutaHistoricoSecundarias = Path.Combine(repoRoot, "data", "historico", "secundarias.json");
                var rutaHistoricoTramoBajo = Path.Combine(repoRoot, "data", "historico", "tramo_bajo.json");
                var rutaOverrides = Path.Combine(repoRoot, "data", "overrides.json");
                var rutaInformes = Path.Combine(repoRoot, "data", "informes.json");

                var ayer = DateOnly.FromDateTime(AhoraChile()).AddDays(-1);
                var informesExistentes = CargarInformesConMigracion(rutaInformes);
                var fechasAProcesar = InformeDiario.CalcularFechasAProcesar(
                    informesExistentes, opciones.Fecha, ayer, criterios.VentanaRecuperacionDias, criterios.TopeFechasRecuperacion);

                if (fechasAProcesar.Count == 0)
                {
                    Console.WriteLine($"[recuperacion] nada que procesar: ya existe informe para {FormatearFecha(ayer)} o posterior.");
                }

                DateOnly? fechaMasRecienteProcesada = null;
                var huboFallo = false;
                var llamadasEnriquecimientoLote = 0;

                foreach (var fechaDelLoop in fechasAProcesar)
                {
                    try
                    {
                        llamadasEnriquecimientoLote += await ProcesarFechaAsync(
                            repoRoot, fechaDelLoop, criterios, cacheUnspsc, catalogoUnspsc, cliente, tiposPrivados,
                            rutaPrioritarias, rutaSecundarias, rutaTramoBajo,
                            rutaHistoricoPrioritarias, rutaHistoricoSecundarias, rutaHistoricoTramoBajo,
                            rutaOverrides, rutaCacheUnspsc);
                        fechaMasRecienteProcesada ??= fechaDelLoop;
                    }
                    catch (MercadoPublicoApiException ex)
                    {
                        Console.Error.WriteLine(
                            $"[recuperacion] fecha {FormatearFecha(fechaDelLoop)} falló, se detiene el loop (las fechas ya procesadas antes quedan persistidas): {ex.Message}");
                        huboFallo = true;
                        break;
                    }
                }

                // Barrido `activas`: una sola vez por invocación (ver
                // DecidirBarridoActivas) — no está atado a ningún lote diario
                // puntual, así que correrlo una vez por fecha recuperada
                // pediría la misma foto de hoy varias veces sin sentido.
                var fechaActivas = DateOnly.FromDateTime(AhoraChile());
                var (corresponde, motivoActivas) = DecidirBarridoActivas(repoRoot);
                ResultadoFiltro? resultadoActivas = null;
                var enriquecidosActivasHoy = 0;
                var enriquecimientosFallidosActivasHoy = 0;
                var ahorradosPorAcumuladosActivas = 0;
                string estadoActivas;
                var nuevasPrioritariasActivas = new List<Candidata>();
                var nuevasSecundariasActivas = new List<Candidata>();
                var nuevasTramoBajoActivas = new List<Candidata>();

                // codigosConfirmados recalculado fresco (no el de antes del
                // loop): refleja también lo que el recupero de fechas haya
                // confirmado recién esta misma corrida.
                var candidatasAntesDeActivas = JsonStore.CargarOPredeterminado(rutaPrioritarias, JsonOpciones.Persistencia, new List<Candidata>());
                var codigosConfirmadosActuales = candidatasAntesDeActivas
                    .Where(c => c.EstadoFlujo != EstadoFlujo.Descartada)
                    .Select(c => c.Codigo)
                    .ToHashSet();

                if (corresponde)
                {
                    // Asimetría deliberada: un fallo aquí NUNCA es fatal para el resto del pipeline.
                    try
                    {
                        var respuestaActivas = await cliente.ObtenerActivasAsync();
                        GuardarRawActivas(repoRoot, fechaActivas, respuestaActivas);

                        (resultadoActivas, enriquecidosActivasHoy, enriquecimientosFallidosActivasHoy, ahorradosPorAcumuladosActivas) =
                            await FiltrarConEnriquecimientoAsync(
                                respuestaActivas.Listado, criterios, cacheUnspsc, catalogoUnspsc, codigosConfirmadosActuales, cliente);
                        estadoActivas = $"corrió ({motivoActivas})";

                        // Merge solo de `activas` (detectadasDiario vacío):
                        // el lote diario de cada fecha del hueco ya se
                        // fusionó por su cuenta dentro de ProcesarFechaAsync.
                        var (todasPrioritariasSinOverride, _, nuevasPA) = FusionarLista(
                            rutaPrioritarias, Array.Empty<CandidataDetectada>(), resultadoActivas.Prioritarias,
                            fechaActivas, fechaActivas, tramo: null, tiposPrivados, CargarMotivoCierreHistorico(rutaHistoricoPrioritarias));
                        var (todasSecundariasSinOverride, _, nuevasSA) = FusionarLista(
                            rutaSecundarias, Array.Empty<CandidataDetectada>(), resultadoActivas.Secundarias,
                            fechaActivas, fechaActivas, tramo: null, tiposPrivados, CargarMotivoCierreHistorico(rutaHistoricoSecundarias));
                        var (todasTramoBajoConActivas, _, nuevasTA) = FusionarLista(
                            rutaTramoBajo, Array.Empty<CandidataDetectada>(), resultadoActivas.TramoBajo,
                            fechaActivas, fechaActivas, tramo: "bajo", tiposPrivados, CargarMotivoCierreHistorico(rutaHistoricoTramoBajo));

                        nuevasPrioritariasActivas = nuevasPA;
                        nuevasSecundariasActivas = nuevasSA;
                        nuevasTramoBajoActivas = nuevasTA;

                        var overridesActuales = JsonStore.CargarOPredeterminado(
                            rutaOverrides, JsonOpciones.Persistencia, new Dictionary<string, EntradaOverride>());
                        var (todasPrioritariasConActivas, todasSecundariasConActivas, _) = AplicadorOverrides.Aplicar(
                            overridesActuales, todasPrioritariasSinOverride, todasSecundariasSinOverride);

                        JsonStore.Guardar(rutaPrioritarias, todasPrioritariasConActivas, JsonOpciones.Persistencia);
                        JsonStore.Guardar(rutaSecundarias, todasSecundariasConActivas, JsonOpciones.Persistencia);
                        JsonStore.Guardar(rutaTramoBajo, todasTramoBajoConActivas, JsonOpciones.Persistencia);
                    }
                    catch (MercadoPublicoApiException ex)
                    {
                        estadoActivas = $"omitido este día — {ex.Message}";
                        Console.Error.WriteLine($"[ADVERTENCIA] Barrido 'activas' omitido este día: {ex.Message}");
                    }
                }
                else
                {
                    estadoActivas = $"no corresponde hoy ({motivoActivas})";
                }

                if (cacheUnspsc.Count != cacheUnspscCountInicial)
                {
                    var cacheOrdenado = cacheUnspsc.Values.OrderBy(e => e.CodigoExterno, StringComparer.Ordinal).ToList();
                    JsonStore.Guardar(rutaCacheUnspsc, cacheOrdenado, JsonOpciones.Persistencia);
                }

                // Re-verificación contra el detalle real de la API: una sola
                // vez por invocación (mismo criterio que `activas` — opera
                // sobre el inventario VIVO actual, no sobre un lote de un
                // día; repetirla por fecha recuperada reverificaría los
                // mismos códigos N veces sin beneficio). Reutiliza `cliente`
                // en vez de un segundo HttpClient aparte.
                //
                // ahora = AhoraChile(), NUNCA DateTime.UtcNow: FechaCierre es
                // hora de Chile SIN ZONA (tal cual la devuelve la API) y la
                // corrida real aterriza cerca de medianoche CLT (rango de
                // atraso del cron ya documentado) — comparar esa fecha
                // contra un "ahora" en UTC compararía naranjas con manzanas.
                var ahora = AhoraChile();

                async Task<DetalleLicitacion?> ObtenerDetalleReverificacion(string codigo, CancellationToken ct)
                {
                    var respuesta = await cliente.ObtenerDetalleAsync(codigo, ct);
                    return respuesta.Listado.FirstOrDefault(l => l.CodigoExterno == codigo);
                }

                var prioritariasFinal = JsonStore.CargarOPredeterminado(rutaPrioritarias, JsonOpciones.Persistencia, new List<Candidata>());
                var secundariasFinal = JsonStore.CargarOPredeterminado(rutaSecundarias, JsonOpciones.Persistencia, new List<Candidata>());
                var tramoBajoFinal = JsonStore.CargarOPredeterminado(rutaTramoBajo, JsonOpciones.Persistencia, new List<Candidata>());

                var estadosTriagePrioritarias = new HashSet<EstadoFlujo>
                {
                    EstadoFlujo.Candidata, EstadoFlujo.Scorecard, EstadoFlujo.Enviada,
                };
                var prioritariasAReverificar = prioritariasFinal
                    .Where(c => c.EstadoFlujo is EstadoFlujo.Pendiente or EstadoFlujo.Candidata
                        or EstadoFlujo.Scorecard or EstadoFlujo.Enviada)
                    .ToList();

                var resultadoPrioritarias = await Verificacion.ReverificacionService.ReverificarCandidatasAsync(
                    ObtenerDetalleReverificacion, prioritariasAReverificar, estadosTriagePrioritarias, ahora);

                var seleccionSecundariasTramoBajo = Verificacion.ReverificacionService.SeleccionarTope(
                    secundariasFinal, tramoBajoFinal, ahora, criterios.TopeReverificacionVencidas);

                var resultadoSecundariasTramoBajo = await Verificacion.ReverificacionService.ReverificarCandidatasAsync(
                    ObtenerDetalleReverificacion, seleccionSecundariasTramoBajo, new HashSet<EstadoFlujo>(), ahora);

                var terminalPrioritarias = resultadoPrioritarias.PasaronATerminal;
                var terminalSecundarias = resultadoSecundariasTramoBajo.PasaronATerminal
                    .Where(c => secundariasFinal.Contains(c)).ToList();
                var terminalTramoBajo = resultadoSecundariasTramoBajo.PasaronATerminal
                    .Where(c => tramoBajoFinal.Contains(c)).ToList();

                if (terminalPrioritarias.Count > 0)
                {
                    prioritariasFinal = prioritariasFinal.Except(terminalPrioritarias).ToList();
                    var historico = JsonStore.CargarOPredeterminado(rutaHistoricoPrioritarias, JsonOpciones.Persistencia, new List<Candidata>());
                    historico.AddRange(terminalPrioritarias);
                    JsonStore.Guardar(rutaHistoricoPrioritarias, historico, JsonOpciones.Persistencia);
                }

                if (terminalSecundarias.Count > 0)
                {
                    secundariasFinal = secundariasFinal.Except(terminalSecundarias).ToList();
                    var historico = JsonStore.CargarOPredeterminado(rutaHistoricoSecundarias, JsonOpciones.Persistencia, new List<Candidata>());
                    historico.AddRange(terminalSecundarias);
                    JsonStore.Guardar(rutaHistoricoSecundarias, historico, JsonOpciones.Persistencia);
                }

                if (terminalTramoBajo.Count > 0)
                {
                    tramoBajoFinal = tramoBajoFinal.Except(terminalTramoBajo).ToList();
                    var historico = JsonStore.CargarOPredeterminado(rutaHistoricoTramoBajo, JsonOpciones.Persistencia, new List<Candidata>());
                    historico.AddRange(terminalTramoBajo);
                    JsonStore.Guardar(rutaHistoricoTramoBajo, historico, JsonOpciones.Persistencia);
                }

                var totalVerificadas = resultadoPrioritarias.Verificadas + resultadoSecundariasTramoBajo.Verificadas;
                if (totalVerificadas > 0)
                {
                    JsonStore.Guardar(rutaPrioritarias, prioritariasFinal, JsonOpciones.Persistencia);
                    JsonStore.Guardar(rutaSecundarias, secundariasFinal, JsonOpciones.Persistencia);
                    JsonStore.Guardar(rutaTramoBajo, tramoBajoFinal, JsonOpciones.Persistencia);
                }

                var reverificadasHoy = totalVerificadas;
                var reverificacionesConCambioFecha = resultadoPrioritarias.CambiosFecha + resultadoSecundariasTramoBajo.CambiosFecha;
                var reverificacionesATerminal = terminalPrioritarias.Count + terminalSecundarias.Count + terminalTramoBajo.Count;
                var reverificacionesFallidas = resultadoPrioritarias.Fallidas + resultadoSecundariasTramoBajo.Fallidas;
                var cierresDetectadosEnTriage =
                    resultadoPrioritarias.CierresDetectadosEnTriage + resultadoSecundariasTramoBajo.CierresDetectadosEnTriage;
                var llamadasReverificacion = resultadoPrioritarias.Verificadas + resultadoPrioritarias.Fallidas
                    + resultadoSecundariasTramoBajo.Verificadas + resultadoSecundariasTramoBajo.Fallidas;

                var eventosReverificacion = resultadoPrioritarias.Eventos.Concat(resultadoSecundariasTramoBajo.Eventos).ToList();
                if (eventosReverificacion.Count > 0)
                {
                    var rutaEventos = Path.Combine(repoRoot, "data", "eventos.json");
                    var eventos = JsonStore.CargarOPredeterminado(rutaEventos, JsonOpciones.Persistencia, new List<EventoAuditoria>());
                    eventos.AddRange(eventosReverificacion);
                    JsonStore.Guardar(rutaEventos, eventos, JsonOpciones.Persistencia);
                }

                Console.WriteLine(
                    $"[reverificacion] prioritarias: verificadas={resultadoPrioritarias.Verificadas} " +
                    $"cambios_fecha={resultadoPrioritarias.CambiosFecha} a_terminal={terminalPrioritarias.Count} " +
                    $"triage={resultadoPrioritarias.CierresDetectadosEnTriage} fallidas={resultadoPrioritarias.Fallidas} | " +
                    $"secundarias+tramo_bajo (tope={criterios.TopeReverificacionVencidas}, " +
                    $"seleccionadas={seleccionSecundariasTramoBajo.Count}): verificadas={resultadoSecundariasTramoBajo.Verificadas} " +
                    $"cambios_fecha={resultadoSecundariasTramoBajo.CambiosFecha} " +
                    $"a_terminal={terminalSecundarias.Count + terminalTramoBajo.Count} fallidas={resultadoSecundariasTramoBajo.Fallidas}");

                var candidatasVerificadasEstaCorrida = prioritariasAReverificar.Concat(seleccionSecundariasTramoBajo).ToList();
                var histogramaEstadoMp = candidatasVerificadasEstaCorrida
                    .Where(c => c.EstadoMp.HasValue)
                    .GroupBy(c => c.EstadoMp!.Value)
                    .OrderBy(g => g.Key)
                    .ToDictionary(g => g.Key, g => g.Count());
                var publicadaPeroVencida = candidatasVerificadasEstaCorrida.Count(c =>
                    c.EstadoMp == 5 && c.FechaCierre is not null && c.FechaCierre.Value < ahora);

                Console.WriteLine(
                    $"[reverificacion] publicada_pero_vencida={publicadaPeroVencida} | histograma CodigoEstado: " +
                    (histogramaEstadoMp.Count == 0
                        ? "(ninguno)"
                        : string.Join(", ", histogramaEstadoMp.Select(kv => $"{kv.Key}={kv.Value}"))));

                var barridoActivasFunnel = resultadoActivas is null
                    ? null
                    : new InformeFunnel(
                        resultadoActivas.Total, resultadoActivas.TrasEstado, resultadoActivas.TrasTipo,
                        resultadoActivas.TrasRegion, resultadoActivas.DescarteDuro,
                        resultadoActivas.Prioritarias.Count, resultadoActivas.Secundarias.Count, resultadoActivas.TramoBajo.Count,
                        nuevasPrioritariasActivas.Count, nuevasSecundariasActivas.Count, nuevasTramoBajoActivas.Count,
                        enriquecidosActivasHoy, enriquecimientosFallidosActivasHoy,
                        resultadoActivas.Bienes, resultadoActivas.SinResolverUnspsc,
                        resultadoActivas.RevisionManualUnspsc, ahorradosPorAcumuladosActivas);

                var llamadasEnriquecimientoActivas = enriquecidosActivasHoy + enriquecimientosFallidosActivasHoy;
                var llamadasBarridoActivas = resultadoActivas is null ? 0 : 1;

                // A qué fecha de informes.json se le atribuyen activas +
                // re-verificación: la más reciente procesada con éxito en
                // este loop (orden descendente de CalcularFechasAProcesar,
                // así que es la primera fecha que tuvo éxito), o si el loop
                // no procesó ninguna (ya al día, o falló en la primera),
                // la fecha más reciente que YA existía — Fusionar congela el
                // embudo de esa fecha tal cual estaba y solo suma los
                // contadores de seguimiento/cuota, sin código especial.
                DateOnly? fechaParaInforme = fechaMasRecienteProcesada
                    ?? (informesExistentes.Count > 0 ? informesExistentes.Max(i => i.Fecha) : (DateOnly?)null);

                List<InformeDiario> informesFinal;

                if (fechaParaInforme is { } fechaInforme)
                {
                    var informeActivasYReverificacion = new InformeDiario(
                        Fecha: fechaInforme,
                        Total: 0, TrasEstado: 0, TrasTipo: 0, TrasRegion: 0, DescarteDuro: 0,
                        Prioritarias: 0, Secundarias: 0, TramoBajo: 0,
                        NuevasPrioritarias: 0, NuevasSecundarias: 0, NuevasTramoBajo: 0,
                        BarridoActivas: barridoActivasFunnel,
                        EnriquecidosHoy: 0, EnriquecimientosFallidos: 0,
                        Bienes: 0, SinResolverUnspsc: 0, RevisionManualUnspsc: 0, AhorradosPorAcumulados: 0,
                        ReverificadasHoy: reverificadasHoy,
                        ReverificacionesConCambioFecha: reverificacionesConCambioFecha,
                        ReverificacionesATerminal: reverificacionesATerminal,
                        ReverificacionesFallidas: reverificacionesFallidas,
                        CierresDetectadosEnTriage: cierresDetectadosEnTriage,
                        LlamadasEnriquecimiento: llamadasEnriquecimientoActivas,
                        LlamadasReverificacion: llamadasReverificacion,
                        LlamadasBarridoActivas: llamadasBarridoActivas);

                    informesFinal = ActualizarSerieInformes(repoRoot, informeActivasYReverificacion);
                    JsonStore.Guardar(rutaInformes, informesFinal, JsonOpciones.Persistencia);

                    var totalNuevasActivas = nuevasPrioritariasActivas.Count + nuevasSecundariasActivas.Count + nuevasTramoBajoActivas.Count;
                    RegistrarEvento(repoRoot, informeActivasYReverificacion, totalNuevasActivas, reverificacionesATerminal);
                }
                else
                {
                    Console.WriteLine(
                        "[recuperacion] sin fecha a la cual atribuir activas/reverificación esta corrida " +
                        "(ningún lote se procesó con éxito y no hay historial previo).");
                    informesFinal = informesExistentes;
                }

                var prioritariasParaDashboard = JsonStore.CargarOPredeterminado(rutaPrioritarias, JsonOpciones.Persistencia, new List<Candidata>());
                var secundariasParaDashboard = JsonStore.CargarOPredeterminado(rutaSecundarias, JsonOpciones.Persistencia, new List<Candidata>());
                var tramoBajoParaDashboard = JsonStore.CargarOPredeterminado(rutaTramoBajo, JsonOpciones.Persistencia, new List<Candidata>());
                var dashboard = GeneradorDashboard.Construir(
                    prioritariasParaDashboard, secundariasParaDashboard, tramoBajoParaDashboard, informesFinal, DateTime.UtcNow);
                JsonStore.Guardar(Path.Combine(repoRoot, "docs", "data.json"), dashboard, JsonOpciones.Persistencia);
                PublicarPanelRevisionConfig(repoRoot);

                ImprimirResumenActivas(estadoActivas, resultadoActivas, enriquecidosActivasHoy, enriquecimientosFallidosActivasHoy, ahorradosPorAcumuladosActivas);

                var llamadasEnriquecimientoTotal = llamadasEnriquecimientoLote + llamadasEnriquecimientoActivas;
                var llamadasTotales = llamadasEnriquecimientoTotal + llamadasReverificacion + llamadasBarridoActivas;
                Console.WriteLine(
                    $"Cuota de llamadas de detalle: enriquecimiento={llamadasEnriquecimientoTotal} " +
                    $"reverificacion={llamadasReverificacion} barrido_activas={llamadasBarridoActivas} total={llamadasTotales}");

                return huboFallo ? 1 : 0;
            }
        }
        catch (MercadoPublicoApiException ex)
        {
            Console.Error.WriteLine($"[FATAL] Pipeline abortado sin escribir cambios: {ex.Message}");
            return 1;
        }
    }

    // Cuerpo de una fecha del recupero de fechas faltantes (2026-10-01,
    // extraído de Main para que el loop multi-fecha quede legible).
    // Completamente autocontenido: lee y escribe disco por su cuenta
    // (candidatas.json/secundarias.json/tramo_bajo.json/históricos/
    // informes.json/eventos.json), así que es seguro llamarlo repetidas
    // veces en el mismo loop — cada llamada ve en disco exactamente lo que
    // la anterior dejó. `codigosConfirmados` se recalcula al principio de
    // CADA llamada (no se recibe precalculado): una Prioritaria confirmada
    // en una fecha anterior del mismo recupero debe excluirse de
    // enriquecimiento al procesar la fecha siguiente.
    //
    // Lanza MercadoPublicoApiException si el fetch del lote diario o de
    // adjudicadas falla — fatal para ESTA fecha, no deja nada parcial
    // (ambos se piden antes de persistir cualquier cosa, mismo criterio ya
    // vigente para una corrida de una sola fecha). El llamador decide qué
    // hacer con esa excepción (detener el loop sin perder las fechas ya
    // procesadas antes, ver Main).
    //
    // Devuelve las llamadas de enriquecimiento consumidas por esta fecha
    // (éxitos + fallidas), para que el llamador pueda sumar la cuota total
    // de la corrida sin repetir el cálculo.
    private static async Task<int> ProcesarFechaAsync(
        string repoRoot,
        DateOnly fecha,
        Criterios criterios,
        Dictionary<string, EntradaCacheUnspsc> cacheUnspsc,
        CatalogoUnspsc catalogoUnspsc,
        MercadoPublicoClient cliente,
        HashSet<string> tiposPrivados,
        string rutaPrioritarias,
        string rutaSecundarias,
        string rutaTramoBajo,
        string rutaHistoricoPrioritarias,
        string rutaHistoricoSecundarias,
        string rutaHistoricoTramoBajo,
        string rutaOverrides,
        string rutaCacheUnspsc)
    {
        var candidatasExistentes = JsonStore.CargarOPredeterminado(
            rutaPrioritarias, JsonOpciones.Persistencia, new List<Candidata>());
        var codigosConfirmados = candidatasExistentes
            .Where(c => c.EstadoFlujo != EstadoFlujo.Descartada)
            .Select(c => c.Codigo)
            .ToHashSet();

        var cacheUnspscCountAntes = cacheUnspsc.Count;

        // Diario + adjudicada: fatal si cualquiera falla, nada se persiste de esta fecha.
        var respuestaDiaria = await cliente.ObtenerListadoDiarioAsync(fecha);
        var respuestaAdjudicada = await cliente.ObtenerAdjudicadasAsync(fecha);

        GuardarRawDelDia(repoRoot, fecha, respuestaDiaria);
        AcumularAdjudicadas(repoRoot, fecha, respuestaAdjudicada.Listado);
        var loteDiario = respuestaDiaria.Listado;

        var (resultadoDiario, enriquecidosHoy, enriquecimientosFallidosHoy, ahorradosPorAcumulados) =
            await FiltrarConEnriquecimientoAsync(loteDiario, criterios, cacheUnspsc, catalogoUnspsc, codigosConfirmados, cliente);

        if (cacheUnspsc.Count != cacheUnspscCountAntes)
        {
            var cacheOrdenado = cacheUnspsc.Values.OrderBy(e => e.CodigoExterno, StringComparer.Ordinal).ToList();
            JsonStore.Guardar(rutaCacheUnspsc, cacheOrdenado, JsonOpciones.Persistencia);
        }

        var (todasPrioritariasSinOverride, nuevasPrioritariasDiario, _) = FusionarLista(
            rutaPrioritarias, resultadoDiario.Prioritarias, null, fecha, fecha, tramo: null,
            tiposPrivados, CargarMotivoCierreHistorico(rutaHistoricoPrioritarias));
        var (todasSecundariasSinOverride, nuevasSecundariasDiario, _) = FusionarLista(
            rutaSecundarias, resultadoDiario.Secundarias, null, fecha, fecha, tramo: null,
            tiposPrivados, CargarMotivoCierreHistorico(rutaHistoricoSecundarias));
        var (todasTramoBajo, nuevasTramoBajoDiario, _) = FusionarLista(
            rutaTramoBajo, resultadoDiario.TramoBajo, null, fecha, fecha, tramo: "bajo",
            tiposPrivados, CargarMotivoCierreHistorico(rutaHistoricoTramoBajo));

        var overrides = JsonStore.CargarOPredeterminado(
            rutaOverrides, JsonOpciones.Persistencia, new Dictionary<string, EntradaOverride>());
        var (todasPrioritarias, todasSecundarias, _) = AplicadorOverrides.Aplicar(
            overrides, todasPrioritariasSinOverride, todasSecundariasSinOverride);

        JsonStore.Guardar(rutaPrioritarias, todasPrioritarias, JsonOpciones.Persistencia);
        JsonStore.Guardar(rutaSecundarias, todasSecundarias, JsonOpciones.Persistencia);
        JsonStore.Guardar(rutaTramoBajo, todasTramoBajo, JsonOpciones.Persistencia);

        var (_, prioritariasMovidas) = RevalidarEstado(rutaPrioritarias, rutaHistoricoPrioritarias, todasPrioritarias, loteDiario);
        var (_, secundariasMovidas) = RevalidarEstado(rutaSecundarias, rutaHistoricoSecundarias, todasSecundarias, loteDiario);
        var (_, tramoBajoMovidas) = RevalidarEstado(rutaTramoBajo, rutaHistoricoTramoBajo, todasTramoBajo, loteDiario);
        var totalMovidasHistorico = prioritariasMovidas + secundariasMovidas + tramoBajoMovidas;

        var llamadasEnriquecimiento = enriquecidosHoy + enriquecimientosFallidosHoy;

        var informeHoy = new InformeDiario(
            Fecha: fecha,
            Total: resultadoDiario.Total, TrasEstado: resultadoDiario.TrasEstado, TrasTipo: resultadoDiario.TrasTipo,
            TrasRegion: resultadoDiario.TrasRegion, DescarteDuro: resultadoDiario.DescarteDuro,
            Prioritarias: resultadoDiario.Prioritarias.Count, Secundarias: resultadoDiario.Secundarias.Count,
            TramoBajo: resultadoDiario.TramoBajo.Count,
            NuevasPrioritarias: nuevasPrioritariasDiario.Count, NuevasSecundarias: nuevasSecundariasDiario.Count,
            NuevasTramoBajo: nuevasTramoBajoDiario.Count,
            BarridoActivas: null,
            EnriquecidosHoy: enriquecidosHoy, EnriquecimientosFallidos: enriquecimientosFallidosHoy,
            Bienes: resultadoDiario.Bienes, SinResolverUnspsc: resultadoDiario.SinResolverUnspsc,
            RevisionManualUnspsc: resultadoDiario.RevisionManualUnspsc, AhorradosPorAcumulados: ahorradosPorAcumulados,
            ReverificadasHoy: 0, ReverificacionesConCambioFecha: 0, ReverificacionesATerminal: 0,
            ReverificacionesFallidas: 0, CierresDetectadosEnTriage: 0,
            LlamadasEnriquecimiento: llamadasEnriquecimiento, LlamadasReverificacion: 0, LlamadasBarridoActivas: 0);

        var informes = ActualizarSerieInformes(repoRoot, informeHoy);
        JsonStore.Guardar(Path.Combine(repoRoot, "data", "informes.json"), informes, JsonOpciones.Persistencia);

        var totalNuevasHoy = nuevasPrioritariasDiario.Count + nuevasSecundariasDiario.Count + nuevasTramoBajoDiario.Count;
        RegistrarEvento(repoRoot, informeHoy, totalNuevasHoy, totalMovidasHistorico);

        ImprimirResumenLoteDiario(
            fecha, resultadoDiario, nuevasPrioritariasDiario.Count, nuevasSecundariasDiario.Count, nuevasTramoBajoDiario.Count,
            enriquecidosHoy, enriquecimientosFallidosHoy, ahorradosPorAcumulados);

        return llamadasEnriquecimiento;
    }

    // config/panel-revision.json (2026-09-22) es config editable por el
    // usuario, mismo patrón que criterios.json — GitHub Pages solo sirve
    // docs/, así que el frontend no puede leerlo directamente de config/.
    // Se publica una copia textual en docs/ cada vez que se regenera
    // docs/data.json (flujo normal + los tres modos de mantenimiento que
    // también regeneran el dashboard), para que editar el archivo y
    // esperar a la próxima corrida (nocturna o de mantenimiento) baste
    // para que el tablero lo recoja, sin lógica de sincronización nueva.
    // Copia textual, no un round-trip por un tipo C#: preserva el archivo
    // tal cual lo dejó el usuario. Si config/panel-revision.json no existe
    // (nunca debería pasar tras este cambio, pero por si se borra a mano),
    // no falla la corrida — docs/app.js ya tiene un fallback hardcodeado.
    private static void PublicarPanelRevisionConfig(string repoRoot)
    {
        var origen = Path.Combine(repoRoot, "config", "panel-revision.json");
        if (!File.Exists(origen))
        {
            return;
        }

        var destino = Path.Combine(repoRoot, "docs", "panel-revision.json");
        File.Copy(origen, destino, overwrite: true);
    }

    private static void GuardarRawDelDia(string repoRoot, DateOnly fecha, ListadoLicitacionesResponse respuesta)
    {
        var ruta = Path.Combine(repoRoot, "data", "raw", $"{FormatearFecha(fecha)}.json");
        JsonStore.Guardar(ruta, respuesta, JsonOpciones.ApiLectura);
    }

    private static void GuardarRawActivas(string repoRoot, DateOnly fechaHoy, ListadoLicitacionesResponse respuesta)
    {
        var ruta = Path.Combine(repoRoot, "data", "raw", $"activas-{FormatearFecha(fechaHoy)}.json");
        JsonStore.Guardar(ruta, respuesta, JsonOpciones.ApiLectura);
    }

    private static void AcumularAdjudicadas(string repoRoot, DateOnly fecha, List<LicitacionRaw> nuevas)
    {
        var ruta = Path.Combine(repoRoot, "data", "adjudicadas", $"{fecha:yyyy-MM}.json");
        var existentes = JsonStore.CargarOPredeterminado(ruta, JsonOpciones.ApiLectura, new List<LicitacionRaw>());

        // GroupBy + Last(): si un código ya existía, se reemplaza por la versión más reciente.
        var combinadas = existentes
            .Concat(nuevas)
            .GroupBy(l => l.CodigoExterno)
            .Select(g => g.Last())
            .OrderBy(l => l.CodigoExterno, StringComparer.Ordinal)
            .ToList();

        JsonStore.Guardar(ruta, combinadas, JsonOpciones.ApiLectura);
    }

    // Por qué existe este barrido (no es para detectar cambios de estado —
    // eso ya lo hace RevalidarEstado con el lote diario crudo): el lote
    // diario solo trae licitaciones con movimiento ESE día. Una publicada
    // el 24 de agosto que cierra el 4 de septiembre no vuelve a aparecer en
    // ningún lote diario posterior a su publicación — el sistema nunca la
    // ve de nuevo. `activas` es el corte transversal que sí la captura,
    // sin importar cuándo se publicó ni si tuvo movimiento reciente. Es lo
    // que motivó agregarlo: sin él, el pipeline es ciego a la mayoría del
    // mercado vigente en cualquier momento dado.
    //
    // Se intenta si es la primera corrida real (aún no existe ningún
    // data/raw/activas-*.json, siembra inicial) o si ya pasó una semana
    // desde la última vez que corrió (2026-10-01, reemplaza "hoy es lunes
    // en Chile": ese chequeo dependía de a qué hora cae la corrida real —
    // atraso de 1h52 a 4h58 ya documentado — así que podía saltarse un
    // lunes completo, y con el recupero de fechas faltantes de más arriba
    // podía además correr dos veces en la misma semana si el loop cruza un
    // lunes de por medio. Un umbral de días transcurridos es objetivo y no
    // depende de qué día calendario le tocó a la corrida).
    private const int UmbralDiasBarridoActivas = 7;

    private static (bool Corresponde, string Motivo) DecidirBarridoActivas(string repoRoot)
    {
        var directorioRaw = Path.Combine(repoRoot, "data", "raw");
        var fechasActivas = Directory.Exists(directorioRaw)
            ? Directory.EnumerateFiles(directorioRaw, "activas-*.json")
                .Select(ruta => RefiltradoService.ExtraerFechaDeArchivo(Path.GetFileName(ruta)))
                .Where(f => f is not null)
                .Select(f => f!.Value)
                .ToList()
            : new List<DateOnly>();
        DateOnly? fechaMasReciente = fechasActivas.Count == 0 ? null : fechasActivas.Max();

        return EvaluarUmbralBarridoActivas(fechaMasReciente, DateOnly.FromDateTime(AhoraChile()), UmbralDiasBarridoActivas);
    }

    // Pura — testeable sin tocar disco. public (a diferencia de
    // MapearEstadoDeCierre, que queda internal): esta sí necesita un test
    // directo propio (pedido explícito del usuario), no alcanza con
    // probarla indirectamente a través de otra función pública — mismo
    // criterio ya usado para promover EvaluarRubro/EsRegionElegible.
    public static (bool Corresponde, string Motivo) EvaluarUmbralBarridoActivas(
        DateOnly? fechaMasReciente, DateOnly hoy, int umbralDias)
    {
        if (fechaMasReciente is null)
        {
            return (true, "primera corrida, siembra inicial");
        }

        var dias = hoy.DayNumber - fechaMasReciente.Value.DayNumber;
        return dias >= umbralDias
            ? (true, $"última activas hace {dias} días (umbral {umbralDias})")
            : (false, $"última activas hace {dias} días, aún no corresponde (umbral {umbralDias})");
    }

    // Toda decisión de "qué día es" (ayer para el lote diario, hoy para el
    // barrido activas, lunes o no para decidir si corre) se calcula en hora
    // de Chile, nunca en la del runner de GitHub Actions ni en UTC crudo:
    // Chile alterna entre UTC-3 y UTC-4 según la época del año, y usar UTC
    // directamente puede pedirle a la API el día equivocado si la corrida
    // cae cerca de la medianoche chilena (ver el comentario del cron en
    // .github/workflows/diario.yml).
    private static DateTime AhoraChile()
    {
        var zonaChile = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zonaChile);
    }

    // Corre las dos etapas puras de FiltroLicitaciones con el filtro de
    // acumulados y el enriquecimiento UNSPSC en el medio: descarte_duro
    // primero (pura), filtro de acumulados (excluye de "Regular" los
    // CodigoExterno ya confirmados como Prioritarias — no gastan ni
    // enriquecimiento ni reclasificación, ver codigosConfirmados en Main),
    // enriquecimiento de lo que sobrevive y todavía no esté en cacheUnspsc
    // (impuro, solo si cliente no es null), y clasificación de rubro al
    // final (pura, sobre el cache ya actualizado). cacheUnspsc se muta
    // in-place: el llamador decide cuándo persistirlo a disco.
    private static async Task<(ResultadoFiltro Resultado, int Enriquecidos, int Fallidos, int AhorradosPorAcumulados)> FiltrarConEnriquecimientoAsync(
        List<LicitacionRaw> lote,
        Criterios criterios,
        Dictionary<string, EntradaCacheUnspsc> cacheUnspsc,
        CatalogoUnspsc catalogoUnspsc,
        IReadOnlySet<string> codigosConfirmados,
        MercadoPublicoClient? cliente,
        CancellationToken ct = default)
    {
        var sobrevivientes = FiltroLicitaciones.FiltrarHastaDescarteDuro(lote, criterios);

        var regularParaProcesar = sobrevivientes.Regular
            .Where(c => !codigosConfirmados.Contains(c.Licitacion.CodigoExterno))
            .ToList();
        var ahorradosPorAcumulados = sobrevivientes.Regular.Count - regularParaProcesar.Count;
        sobrevivientes = sobrevivientes with { Regular = regularParaProcesar };

        var enriquecidos = 0;
        var fallidos = 0;

        if (cliente is not null)
        {
            var faltantes = sobrevivientes.Regular
                .Select(c => c.Licitacion.CodigoExterno)
                .Where(codigo => !cacheUnspsc.ContainsKey(codigo))
                .Distinct()
                .ToList();

            if (faltantes.Count > 0)
            {
                var nuevas = await EnriquecimientoUnspscService.EnriquecerAsync(cliente, faltantes, ct);
                foreach (var entrada in nuevas)
                {
                    cacheUnspsc[entrada.CodigoExterno] = entrada;
                }

                enriquecidos = nuevas.Count;
                fallidos = faltantes.Count - nuevas.Count;
            }
        }

        var resultado = FiltroLicitaciones.ClasificarYFiltrarRubro(sobrevivientes, criterios, cacheUnspsc, catalogoUnspsc);
        return (resultado, enriquecidos, fallidos, ahorradosPorAcumulados);
    }

    private static Candidata CrearCandidata(
        CandidataDetectada detectada, DateOnly fechaLote, OrigenCandidata origen, string? tramo, HashSet<string> tiposPrivados) =>
        new()
        {
            Codigo = detectada.Origen.CodigoExterno,
            Nombre = detectada.Origen.Nombre,
            Tipo = detectada.Tipo,
            FechaCierre = detectada.Origen.FechaCierre,
            FechaLote = fechaLote,
            RubroMatch = detectada.RubroMatch,
            TerminoMatch = detectada.TerminoMatch,
            Region = detectada.Region,
            Organismo = detectada.Organismo,
            Comuna = detectada.Comuna,
            Descripcion = detectada.Descripcion,
            ProhibicionContratacion = detectada.ProhibicionContratacion,
            TipoPago = detectada.TipoPago,
            SubContratacion = detectada.SubContratacion,
            Moneda = detectada.Moneda,
            Monto = detectada.Monto,
            CantidadReclamos = detectada.CantidadReclamos,
            // RevisionAmbigua (2026-09-19): solo al crear la candidata —
            // nunca se recalcula después (ver FiltroLicitaciones y
            // EstadoFlujo.RevisionAmbigua). Un humano la resuelve desde el
            // tablero, lo que escribe un override, no cambiando este campo.
            EstadoFlujo = detectada.EsRevisionAmbigua ? EstadoFlujo.RevisionAmbigua : EstadoFlujo.Pendiente,
            Notas = null,
            ClienteAsignado = null,
            Origen = origen,
            Tramo = tramo,
            TipoPrivado = tiposPrivados.Contains(detectada.Tipo),
            UnspscEstado = detectada.UnspscEstado,
            CodigosProductoUnspsc = detectada.CodigosProductoUnspsc?.ToList() ?? new List<int>(),
            ItemsUnspsc = detectada.ItemsUnspsc?.ToList() ?? new List<ItemUnspscCache>(),
        };

    // Mismo merge/dedupe para las tres listas (Prioritarias -> candidatas.json,
    // Secundarias -> secundarias.json, TramoBajo -> tramo_bajo.json): lo
    // detectado en el barrido `activas` que ya esté en el archivo existente o
    // ya haya salido del lote diario de hoy no se duplica — es lo que permite
    // medir después cuántas se habrían perdido sin el barrido, generalizado a
    // las tres listas por igual.
    //
    // NO escribe a disco (2026-09-19, antes sí lo hacía): AplicadorOverrides
    // necesita corregir el destino de Prioritarias/Secundarias ANTES de que
    // cualquiera de las dos toque disco, así que el llamador (Main) persiste
    // explícitamente después de aplicar overrides.
    private static (List<Candidata> Todas, List<Candidata> NuevasDiario, List<Candidata> NuevasActivas) FusionarLista(
        string rutaArchivo,
        IReadOnlyList<CandidataDetectada> detectadasDiario,
        IReadOnlyList<CandidataDetectada>? detectadasActivas,
        DateOnly fechaDiario,
        DateOnly fechaActivas,
        string? tramo,
        HashSet<string> tiposPrivados,
        IReadOnlyDictionary<string, string?> motivoCierrePorCodigoHistorico)
    {
        var existentes = JsonStore.CargarOPredeterminado(rutaArchivo, JsonOpciones.Persistencia, new List<Candidata>());
        var codigosExistentes = existentes.Select(c => c.Codigo).ToHashSet();
        var codigosDiario = detectadasDiario.Select(c => c.Origen.CodigoExterno).ToHashSet();

        // FiltroLicitaciones.EsCodigoNuevo (2026-09-30): un código ya
        // movido a histórico con un cierre CONFIRMADO por la API no se
        // trata como nuevo solo porque reaparece en el feed — ver el
        // comentario de esa función para el caso real (5482-100-LP26) que
        // motivó el fix. Un cierre INFERIDO (no_encontrada_en_api) sí
        // permite reingresar.
        var nuevasDiario = detectadasDiario
            .Where(c => FiltroLicitaciones.EsCodigoNuevo(c.Origen.CodigoExterno, codigosExistentes, motivoCierrePorCodigoHistorico))
            .Select(c => CrearCandidata(c, fechaDiario, OrigenCandidata.Diario, tramo, tiposPrivados))
            .ToList();

        var nuevasActivas = detectadasActivas is null
            ? new List<Candidata>()
            : detectadasActivas
                .Where(c => FiltroLicitaciones.EsCodigoNuevo(c.Origen.CodigoExterno, codigosExistentes, motivoCierrePorCodigoHistorico)
                    && !codigosDiario.Contains(c.Origen.CodigoExterno))
                .Select(c => CrearCandidata(c, fechaActivas, OrigenCandidata.Activas, tramo, tiposPrivados))
                .ToList();

        var todas = existentes.Concat(nuevasDiario).Concat(nuevasActivas).ToList();

        return (todas, nuevasDiario, nuevasActivas);
    }

    // Carga data/historico/{lista}.json (o predeterminado vacío) y arma el
    // diccionario codigo -> MotivoCierre que FusionarLista necesita para
    // decidir si un código ausente de la lista activa es "nuevo" de verdad
    // (ver FiltroLicitaciones.EsCodigoNuevo). GroupBy+Last: si un código
    // apareciera más de una vez en histórico (no debería pasar hoy), se usa
    // el registro más reciente.
    private static Dictionary<string, string?> CargarMotivoCierreHistorico(string rutaHistorico) =>
        JsonStore.CargarOPredeterminado(rutaHistorico, JsonOpciones.Persistencia, new List<Candidata>())
            .GroupBy(c => c.Codigo)
            .ToDictionary(g => g.Key, g => g.Last().MotivoCierre);

    // Idempotente por fecha: si ya existía una entrada para esa fecha
    // (re-corrida manual vía workflow_dispatch, o un reproceso real como el
    // del 2026-09-05, cuando el cron disparó con retraso y reprocesó el
    // 04-09), la reemplaza en vez de duplicarla.
    //
    // `NuevasPrioritarias`/`NuevasSecundarias`/`NuevasTramoBajo` (y sus
    // equivalentes dentro de `BarridoActivas`) son la ÚNICA excepción: si ya
    // existe un registro para la fecha, se conservan los valores originales
    // en vez de recalcularlos. Son un hecho histórico — cuántas aparecieron
    // por primera vez ese día — no algo derivable del estado actual de
    // candidatas.json/secundarias.json/tramo_bajo.json. Reprocesar una fecha
    // encuentra esos registros ya conocidos (el dedupe los descarta) y
    // recalcularía cualquier "nuevas" a 0 siempre, sin importar cuántas hubo
    // en realidad: exactamente el bug que borró el "3" real del 2026-09-04 el
    // 2026-09-05, ahora generalizado a las tres listas. El resto de los
    // conteos del embudo (total, tras_estado, tras_tipo, descarte_duro,
    // prioritarias, secundarias, tramo_bajo) SÍ describen el estado actual
    // del lote, no un hecho del pasado, así que es correcto que se actualicen
    // en cada reproceso.
    //
    // `BarridoActivas` completo tiene la misma trampa: si el reproceso NO
    // vuelve a correr `activas` ese día (lo normal — `activas` solo corre el
    // primer día o los lunes, ver DecidirBarridoActivas), `informeHoy.
    // BarridoActivas` es null en esa corrida. Escribirlo tal cual borraría un
    // barrido real ya registrado — exactamente como se perdió el bloque con
    // 4.751 registros y 42 nuevas del 2026-09-04 la primera vez. Por eso:
    // sin barrido nuevo, se conserva el bloque existente completo; con
    // barrido nuevo pero sin uno previo, se usa el nuevo tal cual; con ambos,
    // se actualiza el funnel pero se conservan las "nuevas" del existente.
    private static List<InformeDiario> ActualizarSerieInformes(string repoRoot, InformeDiario informeHoy)
    {
        var ruta = Path.Combine(repoRoot, "data", "informes.json");
        var informes = CargarInformesConMigracion(ruta);

        var existente = informes.FirstOrDefault(i => i.Fecha == informeHoy.Fecha);
        var informeAGuardar = InformeDiario.Fusionar(existente, informeHoy);

        informes.RemoveAll(i => i.Fecha == informeHoy.Fecha);
        informes.Add(informeAGuardar);
        return informes.OrderBy(i => i.Fecha).ToList();
    }

    // Cruza una lista activa contra el lote diario CRUDO (antes del filtro de
    // estado, que es justamente lo que nunca se vuelve a mirar una vez que
    // una licitación ya está en candidatas.json/secundarias.json/
    // tramo_bajo.json). Si un código aparece hoy con un estado de cierre
    // (6/7/8) y todavía no tiene triage humano encima (EstadoFlujo ==
    // Pendiente), se marca con el estado correspondiente y se mueve a
    // data/historico/ — no se recalcula ni se pierde, solo deja de estar en
    // la lista activa. Si ya tiene triage humano (Candidata, Enviada,
    // Tomada, etc.), se respeta tal cual y se queda en la lista activa,
    // aunque Mercado Público ya la muestre cerrada: ese trabajo manual no se
    // pisa. No reescribe el archivo activo si no hubo movimientos.
    private static (List<Candidata> Activas, int Movidas) RevalidarEstado(
        string rutaActiva, string rutaHistorico, List<Candidata> listaActual, List<LicitacionRaw> loteDiarioCrudo)
    {
        var estadoPorCodigo = loteDiarioCrudo
            .GroupBy(l => l.CodigoExterno)
            .ToDictionary(g => g.Key, g => g.Last().CodigoEstado);

        var activas = new List<Candidata>();
        var movidas = new List<Candidata>();

        foreach (var candidata in listaActual)
        {
            if (candidata.EstadoFlujo == EstadoFlujo.Pendiente
                && estadoPorCodigo.TryGetValue(candidata.Codigo, out var codigoEstado)
                && MapearEstadoDeCierre(codigoEstado) is { } estadoCierre)
            {
                candidata.EstadoFlujo = estadoCierre;
                movidas.Add(candidata);
            }
            else
            {
                activas.Add(candidata);
            }
        }

        if (movidas.Count > 0)
        {
            var historico = JsonStore.CargarOPredeterminado(rutaHistorico, JsonOpciones.Persistencia, new List<Candidata>());
            historico.AddRange(movidas);
            JsonStore.Guardar(rutaHistorico, historico, JsonOpciones.Persistencia);
            JsonStore.Guardar(rutaActiva, activas, JsonOpciones.Persistencia);
        }

        return (activas, movidas.Count);
    }

    // Promovida de private a internal (2026-09-30, mismo patrón que
    // EvaluarRubro/EsRegionElegible se promovieron a public en
    // FiltroLicitaciones): Verificacion.ReverificacionService.
    // ClasificarEstadoApi la reusa tal cual para 6/7/8, sin duplicar el
    // mapeo. internal (no public) alcanza: ambos viven en el mismo
    // ensamblado, y esta función nunca necesita ser testeable desde fuera
    // de la solución (ClasificarEstadoApi, que sí es public, es la
    // superficie testeable real).
    internal static EstadoFlujo? MapearEstadoDeCierre(int codigoEstado) => codigoEstado switch
    {
        6 => EstadoFlujo.Cerrada,
        7 => EstadoFlujo.Desierta,
        8 => EstadoFlujo.Adjudicada,
        _ => null,
    };

    // Upgrade de una sola vez para informes.json escrito antes del rediseño
    // de listas (Lista A única -> Prioritarias/Secundarias/TramoBajo).
    // Deserializar una entrada vieja directamente contra el InformeDiario
    // nuevo dejaría prioritarias/descarte_duro/nuevas_prioritarias en 0 (los
    // nombres de campo no calzan: la entrada vieja trae "candidatas"/
    // "excluidas"/"nuevas") y la siguiente escritura los borraría en
    // silencio — exactamente el tipo de pérdida de historia de calibración
    // que ya costó una restauración manual una vez (ver PR #6, informe del
    // 2026-09-04). Por eso se renombran los campos antes de deserializar, en
    // vez de confiar en que System.Text.Json rellene con el default. Una
    // entrada que ya tiene "prioritarias" no se toca. "observaciones" se
    // descarta: es una lista, no un conteo, y su reemplazo (secundarias con
    // rubro_match) ya existe hacia adelante — no hay una forma correcta de
    // reconstruir retroactivamente en qué secundaria se habría convertido
    // cada observación vieja.
    private static List<InformeDiario> CargarInformesConMigracion(string ruta)
    {
        if (!File.Exists(ruta))
        {
            return new List<InformeDiario>();
        }

        var nodo = JsonNode.Parse(File.ReadAllText(ruta))?.AsArray()
            ?? throw new InvalidOperationException($"'{ruta}' no deserializó a un arreglo JSON.");

        foreach (var item in nodo)
        {
            if (item is JsonObject informe)
            {
                MigrarFormaDeInforme(informe);
                MigrarCamposUnspsc(informe);
                MigrarCamposRevisionManualYAcumulados(informe);
                MigrarCamposReverificacion(informe);
                MigrarCamposCuotaLlamadas(informe);
            }
        }

        return JsonSerializer.Deserialize<List<InformeDiario>>(nodo.ToJsonString(), JsonOpciones.Persistencia)
            ?? new List<InformeDiario>();
    }

    private static void MigrarFormaDeInforme(JsonObject informe)
    {
        if (informe.ContainsKey("prioritarias"))
        {
            return; // ya está en la forma nueva, nada que migrar
        }

        RenombrarCampo(informe, "candidatas", "prioritarias");
        RenombrarCampo(informe, "excluidas", "descarte_duro");
        RenombrarCampo(informe, "nuevas", "nuevas_prioritarias");
        informe["secundarias"] = 0;
        informe["tramo_bajo"] = 0;
        informe["nuevas_secundarias"] = 0;
        informe["nuevas_tramo_bajo"] = 0;
        informe.Remove("observaciones");

        if (informe["barrido_activas"] is JsonObject barrido)
        {
            MigrarFormaDeInforme(barrido);
        }
    }

    // Upgrade de una sola vez para informes.json escrito antes de F2
    // (enriquecimiento UNSPSC). Guard independiente de MigrarFormaDeInforme
    // ("prioritarias"): una entrada puede ya estar en la forma de tres
    // listas pero seguir sin estos campos, así que no se puede reusar el
    // mismo guard. Se aplica al informe principal Y a barrido_activas si
    // existe — ambos comparten la forma InformeFunnel.
    private static void MigrarCamposUnspsc(JsonObject informe)
    {
        if (!informe.ContainsKey("enriquecidos_hoy"))
        {
            informe["enriquecidos_hoy"] = 0;
            informe["enriquecimientos_fallidos"] = 0;
            informe["bienes"] = 0;
            informe["sin_resolver_unspsc"] = 0;
        }

        if (informe["barrido_activas"] is JsonObject barrido)
        {
            MigrarCamposUnspsc(barrido);
        }
    }

    // Upgrade de una sola vez para informes.json escrito antes de F2
    // (segmento 43/región, 2026-09-17). Guard independiente de
    // MigrarCamposUnspsc ("enriquecidos_hoy"): una entrada ya puede tener
    // ese campo (migrada en la tanda anterior) y seguir sin
    // revision_manual_unspsc/ahorrados_por_acumulados, que se introducen
    // juntos en esta tanda — no hace falta un guard por campo.
    private static void MigrarCamposRevisionManualYAcumulados(JsonObject informe)
    {
        if (!informe.ContainsKey("revision_manual_unspsc"))
        {
            informe["revision_manual_unspsc"] = 0;
            informe["ahorrados_por_acumulados"] = 0;
        }

        if (informe["barrido_activas"] is JsonObject barrido)
        {
            MigrarCamposRevisionManualYAcumulados(barrido);
        }
    }

    // Upgrade de una sola vez para informes.json escrito antes de este
    // follow-up (2026-09-30, ver Verificacion/ReverificacionService.cs).
    // Guard independiente de los anteriores ("reverificadas_hoy"): una
    // entrada puede ya tener todos los campos previos y seguir sin estos 5,
    // que se introducen juntos acá. A diferencia de MigrarCamposUnspsc/
    // MigrarCamposRevisionManualYAcumulados, NO recurre a barrido_activas —
    // estos campos son de InformeDiario únicamente (la re-verificación
    // combina Prioritarias+Secundarias/TramoBajo en un solo conteo por
    // corrida, no por origen lote-diario-vs-activas), InformeFunnel no los
    // declara.
    private static void MigrarCamposReverificacion(JsonObject informe)
    {
        if (!informe.ContainsKey("reverificadas_hoy"))
        {
            informe["reverificadas_hoy"] = 0;
            informe["reverificaciones_con_cambio_fecha"] = 0;
            informe["reverificaciones_a_terminal"] = 0;
            informe["reverificaciones_fallidas"] = 0;
            informe["cierres_detectados_en_triage"] = 0;
        }
    }

    // Upgrade de una sola vez, mismo follow-up (2026-09-30). Guard
    // independiente ("llamadas_enriquecimiento"): el desglose de cuota de
    // llamadas de detalle se introduce junto pero es conceptualmente
    // distinto de los contadores de reverificación de arriba. Mismo
    // criterio que el guard anterior: solo InformeDiario, sin recursión a
    // barrido_activas.
    private static void MigrarCamposCuotaLlamadas(JsonObject informe)
    {
        if (!informe.ContainsKey("llamadas_enriquecimiento"))
        {
            informe["llamadas_enriquecimiento"] = 0;
            informe["llamadas_reverificacion"] = 0;
            informe["llamadas_barrido_activas"] = 0;
        }
    }

    private static void RenombrarCampo(JsonObject obj, string desde, string hacia)
    {
        if (obj.TryGetPropertyValue(desde, out var valor))
        {
            obj.Remove(desde);
            obj[hacia] = valor;
        }
    }

    private static void RegistrarEvento(string repoRoot, InformeDiario informe, int totalNuevas, int totalMovidasHistorico)
    {
        var ruta = Path.Combine(repoRoot, "data", "eventos.json");
        var eventos = JsonStore.CargarOPredeterminado(ruta, JsonOpciones.Persistencia, new List<EventoAuditoria>());

        var detalle = $"total={informe.Total} prioritarias={informe.Prioritarias} " +
            $"secundarias={informe.Secundarias} tramo_bajo={informe.TramoBajo} nuevas={totalNuevas} " +
            $"movidas_historico={totalMovidasHistorico}"
            + (informe.BarridoActivas is { } b
                ? $" activas_total={b.Total} activas_prioritarias={b.Prioritarias} activas_secundarias={b.Secundarias}"
                : string.Empty);

        eventos.Add(new EventoAuditoria(DateTime.UtcNow, "sistema", "corrida_pipeline", null, detalle));
        JsonStore.Guardar(ruta, eventos, JsonOpciones.Persistencia);
    }

    private static string FormatearFecha(DateOnly fecha) =>
        fecha.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    // La primera corrida real estuvo nueve segundos en silencio total sin
    // esto — cuando el pipeline corre automático a diario, este resumen por
    // consola (visible en el log del job de GitHub Actions) es lo único que
    // alguien va a mirar para saber si la corrida tuvo sentido.
    //
    // Separado en dos funciones (2026-10-01, recupero de fechas faltantes):
    // el lote diario ahora puede imprimirse varias veces en una misma
    // corrida (una por fecha recuperada, con la fecha en la cabecera para
    // distinguirlas) mientras que `activas` corre una sola vez por
    // invocación — ya no tiene sentido que una sola función imprima ambos
    // juntos.
    private static void ImprimirResumenLoteDiario(
        DateOnly fecha, ResultadoFiltro resultadoDiario, int nuevasPrioritariasDiario, int nuevasSecundariasDiario, int nuevasTramoBajoDiario,
        int enriquecidosDiario, int enriquecimientosFallidosDiario, int ahorradosPorAcumuladosDiario)
    {
        Console.WriteLine($"== Resumen de la corrida — lote diario {FormatearFecha(fecha)} ==");
        Console.WriteLine(
            $"Lote diario: total={resultadoDiario.Total} tras_estado={resultadoDiario.TrasEstado} " +
            $"tras_tipo={resultadoDiario.TrasTipo} descarte_duro={resultadoDiario.DescarteDuro} " +
            $"prioritarias={resultadoDiario.Prioritarias.Count} (nuevas={nuevasPrioritariasDiario}) " +
            $"secundarias={resultadoDiario.Secundarias.Count} (nuevas={nuevasSecundariasDiario}) " +
            $"tramo_bajo={resultadoDiario.TramoBajo.Count} (nuevas={nuevasTramoBajoDiario}) " +
            $"unspsc: bienes={resultadoDiario.Bienes} revision_manual={resultadoDiario.RevisionManualUnspsc} " +
            $"sin_resolver={resultadoDiario.SinResolverUnspsc} tras_region={resultadoDiario.TrasRegion} " +
            $"enriquecidos_hoy={enriquecidosDiario} enriquecimientos_fallidos={enriquecimientosFallidosDiario} " +
            $"ahorrados_por_acumulados={ahorradosPorAcumuladosDiario}");
    }

    private static void ImprimirResumenActivas(
        string estadoActivas, ResultadoFiltro? resultadoActivas, int enriquecidosActivas, int enriquecimientosFallidosActivas,
        int ahorradosPorAcumuladosActivas)
    {
        Console.WriteLine(resultadoActivas is null
            ? $"Barrido 'activas': {estadoActivas}"
            : $"Barrido 'activas': {estadoActivas} -> total={resultadoActivas.Total} " +
              $"tras_estado={resultadoActivas.TrasEstado} tras_tipo={resultadoActivas.TrasTipo} " +
              $"descarte_duro={resultadoActivas.DescarteDuro} prioritarias={resultadoActivas.Prioritarias.Count} " +
              $"secundarias={resultadoActivas.Secundarias.Count} tramo_bajo={resultadoActivas.TramoBajo.Count} " +
              $"unspsc: bienes={resultadoActivas.Bienes} revision_manual={resultadoActivas.RevisionManualUnspsc} " +
              $"sin_resolver={resultadoActivas.SinResolverUnspsc} tras_region={resultadoActivas.TrasRegion} " +
              $"enriquecidos_hoy={enriquecidosActivas} enriquecimientos_fallidos={enriquecimientosFallidosActivas} " +
              $"ahorrados_por_acumulados={ahorradosPorAcumuladosActivas}");
    }

    // Solo lectura sobre el estado de producción: lee config/criterios.json
    // (o el que se le pase) y data/raw/, y escribe únicamente el CSV de
    // salida. No llama a ninguna función de las que escriben
    // candidatas.json/informes.json/eventos.json/docs/data.json.
    private static int EjecutarRefiltrado(string repoRoot, OpcionesCli opciones)
    {
        if (opciones.RutaCriteriosAlternativos is null)
        {
            Console.Error.WriteLine("[FATAL] --refiltrar requiere --criterios <ruta>.");
            return 1;
        }

        var rutaCriterios = Path.IsPathRooted(opciones.RutaCriteriosAlternativos)
            ? opciones.RutaCriteriosAlternativos
            : Path.Combine(Directory.GetCurrentDirectory(), opciones.RutaCriteriosAlternativos);
        var criterios = JsonStore.Cargar<Criterios>(rutaCriterios, JsonOpciones.Config);

        // Cache/catálogo de producción, de solo lectura — --refiltrar nunca
        // llama a la API (ver RefiltradoService.Ejecutar); un código sin
        // entrada de cache sale PendienteEnriquecimiento en el CSV.
        var catalogoUnspsc = CatalogoUnspsc.CargarDesdeArchivo(
            Path.Combine(repoRoot, "config", "catalogo-unspsc.tsv"));
        var cacheUnspscLista = JsonStore.CargarOPredeterminado(
            Path.Combine(repoRoot, "data", "cache-unspsc.json"), JsonOpciones.Persistencia, new List<EntradaCacheUnspsc>());
        var cacheUnspsc = cacheUnspscLista.ToDictionary(e => e.CodigoExterno);

        var directorioRaw = Path.Combine(repoRoot, "data", "raw");
        var resultado = RefiltradoService.Ejecutar(directorioRaw, criterios, opciones.Desde, cacheUnspsc, catalogoUnspsc);

        var rutaSalida = opciones.RutaSalidaRefiltrado
            ?? $"refiltrado-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        if (!Path.IsPathRooted(rutaSalida))
        {
            rutaSalida = Path.Combine(Directory.GetCurrentDirectory(), rutaSalida);
        }

        File.WriteAllText(rutaSalida, RefiltradoService.GenerarCsv(resultado.Candidatas), new UTF8Encoding(true));

        Console.WriteLine("== Resumen del refiltrado ==");
        Console.WriteLine($"Criterios: {rutaCriterios}");
        Console.WriteLine($"Archivos procesados en data/raw/: {resultado.ArchivosProcesados}");
        Console.WriteLine($"Registros crudos totales: {resultado.RegistrosTotales}");
        Console.WriteLine($"Candidatas encontradas (con duplicados entre archivos): {resultado.CandidatasConDuplicados}");
        Console.WriteLine($"Candidatas únicas tras dedupe: {resultado.Candidatas.Count}");
        Console.WriteLine($"CSV escrito en: {rutaSalida}");

        return 0;
    }

    // Backfill único (2026-09-18): las Prioritarias que ya existían antes de
    // F2 (UNSPSC) quedaron congeladas para siempre por el filtro de
    // acumulados — nunca se reprocesan porque ya están "confirmadas", así
    // que la limpieza que motivó el rediseño UNSPSC nunca les llega. Este
    // modo corre el enriquecimiento y la reclasificación UNSPSC+región sobre
    // el inventario actual de data/candidatas.json, una sola vez, ignorando
    // el filtro de acumulados a propósito (es justamente lo que hay que
    // saltarse). Después de correr, el filtro de acumulados sigue operando
    // normal sobre lo que haya quedado confirmado.
    //
    // Requiere MP_TICKET real — no tiene equivalente a --fixture, no tendría
    // sentido reclasificar el inventario de producción contra datos de
    // prueba.
    private static async Task<int> EjecutarBackfillUnspscAsync(string repoRoot)
    {
        var ticket = Environment.GetEnvironmentVariable("MP_TICKET")
            ?? throw new MercadoPublicoApiException(
                "Falta la variable de entorno MP_TICKET (--backfill-unspsc requiere red real, no tiene modo --fixture).");

        var criterios = JsonStore.Cargar<Criterios>(
            Path.Combine(repoRoot, "config", "criterios.json"), JsonOpciones.Config);
        var catalogoUnspsc = CatalogoUnspsc.CargarDesdeArchivo(
            Path.Combine(repoRoot, "config", "catalogo-unspsc.tsv"));
        var familiasRevisionManual = criterios.FamiliasUnspscRevisionManual.ToHashSet();

        var rutaCacheUnspsc = Path.Combine(repoRoot, "data", "cache-unspsc.json");
        var cacheUnspscInicial = JsonStore.CargarOPredeterminado(
            rutaCacheUnspsc, JsonOpciones.Persistencia, new List<EntradaCacheUnspsc>());
        var cacheUnspsc = cacheUnspscInicial.ToDictionary(e => e.CodigoExterno);
        var cacheUnspscCountInicial = cacheUnspsc.Count;

        var rutaPrioritarias = Path.Combine(repoRoot, "data", "candidatas.json");
        var prioritarias = JsonStore.CargarOPredeterminado(
            rutaPrioritarias, JsonOpciones.Persistencia, new List<Candidata>());

        using var http = new HttpClient();
        var cliente = new MercadoPublicoClient(http, ticket);

        var faltantes = prioritarias
            .Select(c => c.Codigo)
            .Where(codigo => !cacheUnspsc.ContainsKey(codigo))
            .Distinct()
            .ToList();

        var cronometroEnriquecimiento = System.Diagnostics.Stopwatch.StartNew();
        var enriquecidos = 0;
        if (faltantes.Count > 0)
        {
            var nuevas = await EnriquecimientoUnspscService.EnriquecerAsync(cliente, faltantes);
            foreach (var entrada in nuevas)
            {
                cacheUnspsc[entrada.CodigoExterno] = entrada;
            }

            enriquecidos = nuevas.Count;
        }
        cronometroEnriquecimiento.Stop();
        var fallidos = faltantes.Count - enriquecidos;

        if (cacheUnspsc.Count != cacheUnspscCountInicial)
        {
            var cacheOrdenado = cacheUnspsc.Values
                .OrderBy(e => e.CodigoExterno, StringComparer.Ordinal)
                .ToList();
            JsonStore.Guardar(rutaCacheUnspsc, cacheOrdenado, JsonOpciones.Persistencia);
        }

        var seQuedan = new List<Candidata>();
        var seMueven = new List<Candidata>();
        var conteos = new Dictionary<string, int>
        {
            ["confirmada"] = 0,
            ["bien"] = 0,
            ["revision_manual"] = 0,
            ["fuera_de_region"] = 0,
            ["sin_resolver"] = 0,
        };
        // Códigos concretos de sin_resolver (2026-09-19, pedido explícito del
        // usuario): un volumen alto acá es señal de que config/catalogo-
        // unspsc.tsv necesita actualizarse — el mismo tipo de gap ya visto
        // en la investigación de septiembre (1057049-338-LE26, código
        // ausente del catálogo).
        var codigosSinResolver = new List<string>();

        foreach (var candidata in prioritarias)
        {
            cacheUnspsc.TryGetValue(candidata.Codigo, out var entrada);
            var estadoUnspsc = ClasificadorUnspsc.Clasificar(entrada, catalogoUnspsc, familiasRevisionManual);

            candidata.UnspscEstado = estadoUnspsc;
            candidata.Region = entrada?.RegionUnidad;
            candidata.Moneda = entrada?.Moneda;
            candidata.Monto = entrada?.Monto;
            candidata.CantidadReclamos = entrada?.CantidadReclamos;
            candidata.Organismo = entrada?.NombreOrganismo;
            candidata.Comuna = entrada?.ComunaUnidad;
            candidata.Descripcion = entrada?.Descripcion;
            candidata.ProhibicionContratacion = entrada?.ProhibicionContratacion;
            candidata.TipoPago = entrada?.TipoPago;
            candidata.SubContratacion = entrada?.SubContratacion;
            candidata.CodigosProductoUnspsc = entrada?.Items
                .Where(i => i.CodigoProducto is not null)
                .Select(i => i.CodigoProducto!.Value)
                .ToList() ?? new List<int>();
            candidata.ItemsUnspsc = entrada?.Items ?? new List<ItemUnspscCache>();

            // Bien/SinResolver/PendienteEnriquecimiento nunca evalúan rubro
            // (mismo invariante que ClasificarYFiltrarRubro) — se limpia
            // RubroMatch/TerminoMatch heredado del filtro de palabras
            // anterior a F2, que no tiene ninguna validez bajo UNSPSC.
            // RevisionManual y "servicio fuera de región" conservan su
            // RubroMatch/TerminoMatch tal cual: siguen siendo información
            // válida, fue justo lo que las promovió en su momento.
            if (estadoUnspsc == UnspscEstado.Bien)
            {
                candidata.RubroMatch = null;
                candidata.TerminoMatch = null;
                seMueven.Add(candidata);
                conteos["bien"]++;
                continue;
            }

            if (estadoUnspsc is UnspscEstado.SinResolver or UnspscEstado.PendienteEnriquecimiento)
            {
                candidata.RubroMatch = null;
                candidata.TerminoMatch = null;
                seMueven.Add(candidata);
                conteos["sin_resolver"]++;
                codigosSinResolver.Add(candidata.Codigo);
                continue;
            }

            if (estadoUnspsc == UnspscEstado.RevisionManual)
            {
                seMueven.Add(candidata);
                conteos["revision_manual"]++;
                continue;
            }

            // estadoUnspsc == Servicio
            if (FiltroLicitaciones.EsRegionElegible(criterios, candidata.Region))
            {
                seQuedan.Add(candidata);
                conteos["confirmada"]++;
            }
            else
            {
                seMueven.Add(candidata);
                conteos["fuera_de_region"]++;
            }
        }

        JsonStore.Guardar(rutaPrioritarias, seQuedan, JsonOpciones.Persistencia);

        var rutaSecundarias = Path.Combine(repoRoot, "data", "secundarias.json");
        var secundarias = JsonStore.CargarOPredeterminado(
            rutaSecundarias, JsonOpciones.Persistencia, new List<Candidata>());
        var codigosSecundariasExistentes = secundarias.Select(c => c.Codigo).ToHashSet();
        foreach (var candidata in seMueven)
        {
            if (codigosSecundariasExistentes.Add(candidata.Codigo))
            {
                secundarias.Add(candidata);
            }
        }
        JsonStore.Guardar(rutaSecundarias, secundarias, JsonOpciones.Persistencia);

        var tramoBajo = JsonStore.CargarOPredeterminado(
            Path.Combine(repoRoot, "data", "tramo_bajo.json"), JsonOpciones.Persistencia, new List<Candidata>());
        var informesExistentes = CargarInformesConMigracion(Path.Combine(repoRoot, "data", "informes.json"));
        var dashboard = GeneradorDashboard.Construir(seQuedan, secundarias, tramoBajo, informesExistentes, DateTime.UtcNow);
        JsonStore.Guardar(Path.Combine(repoRoot, "docs", "data.json"), dashboard, JsonOpciones.Persistencia);
        PublicarPanelRevisionConfig(repoRoot);

        var detalleEvento = $"total={prioritarias.Count} confirmadas={conteos["confirmada"]} " +
            $"bien={conteos["bien"]} revision_manual={conteos["revision_manual"]} " +
            $"fuera_de_region={conteos["fuera_de_region"]} sin_resolver={conteos["sin_resolver"]}";
        var eventos = JsonStore.CargarOPredeterminado(
            Path.Combine(repoRoot, "data", "eventos.json"), JsonOpciones.Persistencia, new List<EventoAuditoria>());
        eventos.Add(new EventoAuditoria(DateTime.UtcNow, "sistema", "backfill_unspsc", null, detalleEvento));
        JsonStore.Guardar(Path.Combine(repoRoot, "data", "eventos.json"), eventos, JsonOpciones.Persistencia);

        Console.WriteLine("== Resumen del backfill UNSPSC ==");
        Console.WriteLine($"Prioritarias procesadas: {prioritarias.Count}");
        Console.WriteLine($"Confirmadas (siguen en Prioritarias): {conteos["confirmada"]}");
        Console.WriteLine($"Movidas a Secundarias por 'bien': {conteos["bien"]}");
        Console.WriteLine($"Movidas a Secundarias por 'revision_manual': {conteos["revision_manual"]}");
        Console.WriteLine($"Movidas a Secundarias por 'fuera_de_region': {conteos["fuera_de_region"]}");
        Console.WriteLine($"Movidas a Secundarias por 'sin_resolver': {conteos["sin_resolver"]}");
        Console.WriteLine(
            $"Enriquecimiento: llamadas_intentadas={faltantes.Count} exitosas={enriquecidos} " +
            $"fallidas={fallidos} tiempo_total={cronometroEnriquecimiento.Elapsed.TotalSeconds:F1}s " +
            (faltantes.Count > 0
                ? $"promedio={cronometroEnriquecimiento.Elapsed.TotalSeconds / faltantes.Count:F2}s/llamada"
                : "(nada que enriquecer, todo ya estaba en cache)"));
        if (codigosSinResolver.Count > 0)
        {
            Console.WriteLine($"Códigos sin_resolver ({codigosSinResolver.Count}): {string.Join(", ", codigosSinResolver)}");
        }

        return 0;
    }

    // Mantenimiento puntual (2026-09-21): las Prioritarias y Tramo bajo
    // existentes quedaron congeladas con clasificaciones de antes de los
    // cambios de PR #31 (términos ambiguos, rubro en Tramo bajo) — mismo
    // problema que motivó --backfill-unspsc, mismo remedio: revalida
    // Estado (mismo criterio que RevalidarEstado) y reclasifica con las
    // reglas actuales. Secundarias queda intacto por diseño explícito —
    // sigue siendo repositorio sin filtrar.
    //
    // Requiere MP_TICKET real — no tiene equivalente a --fixture.
    private static async Task<int> EjecutarReevaluarInventarioAsync(string repoRoot)
    {
        var ticket = Environment.GetEnvironmentVariable("MP_TICKET")
            ?? throw new MercadoPublicoApiException(
                "Falta la variable de entorno MP_TICKET (--reevaluar-inventario requiere red real, no tiene modo --fixture).");

        var criterios = JsonStore.Cargar<Criterios>(
            Path.Combine(repoRoot, "config", "criterios.json"), JsonOpciones.Config);
        var catalogoUnspsc = CatalogoUnspsc.CargarDesdeArchivo(
            Path.Combine(repoRoot, "config", "catalogo-unspsc.tsv"));
        var familiasRevisionManual = criterios.FamiliasUnspscRevisionManual.ToHashSet();

        var rutaCacheUnspsc = Path.Combine(repoRoot, "data", "cache-unspsc.json");
        var cacheUnspscInicial = JsonStore.CargarOPredeterminado(
            rutaCacheUnspsc, JsonOpciones.Persistencia, new List<EntradaCacheUnspsc>());
        var cacheUnspsc = cacheUnspscInicial.ToDictionary(e => e.CodigoExterno);

        var rutaPrioritarias = Path.Combine(repoRoot, "data", "candidatas.json");
        var rutaTramoBajo = Path.Combine(repoRoot, "data", "tramo_bajo.json");
        var rutaSecundarias = Path.Combine(repoRoot, "data", "secundarias.json");
        var rutaHistoricoPrioritarias = Path.Combine(repoRoot, "data", "historico", "candidatas.json");
        var rutaHistoricoTramoBajo = Path.Combine(repoRoot, "data", "historico", "tramo_bajo.json");

        var prioritarias = JsonStore.CargarOPredeterminado(rutaPrioritarias, JsonOpciones.Persistencia, new List<Candidata>());
        var tramoBajo = JsonStore.CargarOPredeterminado(rutaTramoBajo, JsonOpciones.Persistencia, new List<Candidata>());

        using var http = new HttpClient();
        var cliente = new MercadoPublicoClient(http, ticket);

        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var llamadasIntentadas = 0;
        var llamadasExitosas = 0;
        var cacheModificado = false;

        // Un fallo puntual (agotados los reintentos) nunca es fatal para el
        // resto — misma asimetría ya establecida (EnriquecimientoUnspscService,
        // barrido activas): la candidata se deja tal cual, se reintenta en
        // una corrida futura.
        async Task<DetalleLicitacion?> ObtenerDetalleSeguro(string codigo)
        {
            llamadasIntentadas++;
            try
            {
                var respuesta = await cliente.ObtenerDetalleAsync(codigo);
                llamadasExitosas++;
                return respuesta.Listado.FirstOrDefault(l => l.CodigoExterno == codigo);
            }
            catch (MercadoPublicoApiException ex)
            {
                Console.Error.WriteLine(
                    $"[ADVERTENCIA] Detalle omitido para {codigo}, se reintenta la próxima corrida: {ex.Message}");
                return null;
            }
        }

        var prioritariasActivas = new List<Candidata>();
        var prioritariasHistorico = new List<Candidata>();
        var secundariasNuevas = new List<Candidata>();
        var tramoBajoActivas = new List<Candidata>();
        var tramoBajoHistorico = new List<Candidata>();

        var movidasHistoricoPrioritarias = 0;
        var movidasHistoricoTramoBajo = 0;
        var tramoBajoConRubro = 0;
        var prioritariasConfirmadas = 0;
        var prioritariasDegradadas = 0;

        foreach (var candidata in prioritarias)
        {
            var licitacion = await ObtenerDetalleSeguro(candidata.Codigo);
            if (licitacion is null)
            {
                prioritariasActivas.Add(candidata);
                continue;
            }

            cacheUnspsc[candidata.Codigo] = EnriquecimientoUnspscService.ConstruirEntrada(candidata.Codigo, licitacion);
            cacheModificado = true;

            // Se registra lo que devolvió la API ANTES del guard de
            // triage humano (2026-09-30, ajuste sobre el hotfix de
            // CodigoEstado): nunca toca EstadoFlujo ni mueve de lista,
            // solo dice qué vio la API — así una Scorecard/Candidata/
            // Enviada ya revocada muestra el badge igual que lo hace
            // ReverificarCandidatasAsync, en vez de esperar a la próxima
            // re-verificación automática.
            candidata.EstadoMp = licitacion.CodigoEstado;
            candidata.UltimaVerificacion = AhoraChile();

            if (candidata.EstadoFlujo != EstadoFlujo.Pendiente)
            {
                // Triage humano ya encima — se respeta tal cual, mismo
                // criterio que RevalidarEstado (ni se mueve a histórico ni
                // se reclasifica, aunque Mercado Público ya la muestre
                // cerrada o UNSPSC diga que ya no calificaría).
                prioritariasActivas.Add(candidata);
                continue;
            }

            if (licitacion.CodigoEstado != 5)
            {
                var (resultadoApi, estadoTerminal) = Verificacion.ReverificacionService
                    .ClasificarEstadoApi(licitacion.CodigoEstado, candidata.Codigo);

                if (resultadoApi == Verificacion.ReverificacionService.ResultadoEstadoApi.Suspendida)
                {
                    // 16/19: nunca cierra ni mueve, puede reactivarse —
                    // mismo criterio que ReverificarCandidatasAsync.
                    prioritariasActivas.Add(candidata);
                    continue;
                }

                candidata.EstadoFlujo = estadoTerminal!.Value;
                prioritariasHistorico.Add(candidata);
                movidasHistoricoPrioritarias++;
                continue;
            }

            // Sigue Publicada: reclasificar con las reglas actuales, misma
            // precedencia que ClasificarYFiltrarRubro para Regular.
            var entrada = cacheUnspsc[candidata.Codigo];
            var estadoUnspsc = ClasificadorUnspsc.Clasificar(entrada, catalogoUnspsc, familiasRevisionManual);
            candidata.UnspscEstado = estadoUnspsc;
            candidata.Region = entrada.RegionUnidad;
            candidata.Moneda = entrada.Moneda;
            candidata.Monto = entrada.Monto;
            candidata.CantidadReclamos = entrada.CantidadReclamos;
            candidata.Organismo = entrada.NombreOrganismo;
            candidata.Comuna = entrada.ComunaUnidad;
            candidata.Descripcion = entrada.Descripcion;
            candidata.ProhibicionContratacion = entrada.ProhibicionContratacion;
            candidata.TipoPago = entrada.TipoPago;
            candidata.SubContratacion = entrada.SubContratacion;
            candidata.CodigosProductoUnspsc = entrada.Items
                .Where(i => i.CodigoProducto is not null)
                .Select(i => i.CodigoProducto!.Value)
                .ToList();
            candidata.ItemsUnspsc = entrada.Items;

            bool seMantiene;
            if (estadoUnspsc is UnspscEstado.Bien or UnspscEstado.SinResolver or UnspscEstado.PendienteEnriquecimiento)
            {
                candidata.RubroMatch = null;
                candidata.TerminoMatch = null;
                seMantiene = false;
            }
            else if (estadoUnspsc == UnspscEstado.RevisionManual)
            {
                var (rubroRevision, terminoRevision, _) = FiltroLicitaciones.EvaluarRubro(
                    criterios, TextoNormalizador.Normalizar(candidata.Nombre));
                candidata.RubroMatch = rubroRevision?.Id;
                candidata.TerminoMatch = terminoRevision;
                seMantiene = false;
            }
            else // Servicio
            {
                var (rubro, termino, esAmbiguo) = FiltroLicitaciones.EvaluarRubro(
                    criterios, TextoNormalizador.Normalizar(candidata.Nombre));
                var regionElegible = FiltroLicitaciones.EsRegionElegible(criterios, candidata.Region);
                candidata.RubroMatch = rubro?.Id;
                candidata.TerminoMatch = termino;
                seMantiene = rubro is not null && rubro.Prioridad == "alta" && regionElegible && !esAmbiguo;
            }

            if (seMantiene)
            {
                prioritariasConfirmadas++;
                prioritariasActivas.Add(candidata);
            }
            else
            {
                candidata.EstadoFlujo = EstadoFlujo.RevisionDegradada;
                prioritariasDegradadas++;
                secundariasNuevas.Add(candidata);
            }
        }

        foreach (var candidata in tramoBajo)
        {
            var licitacion = await ObtenerDetalleSeguro(candidata.Codigo);
            if (licitacion is null)
            {
                tramoBajoActivas.Add(candidata);
                continue;
            }

            cacheUnspsc[candidata.Codigo] = EnriquecimientoUnspscService.ConstruirEntrada(candidata.Codigo, licitacion);
            cacheModificado = true;

            // Mismo criterio que en Prioritarias: se registra ANTES del
            // guard de triage humano, sin tocar EstadoFlujo ni mover de
            // lista.
            candidata.EstadoMp = licitacion.CodigoEstado;
            candidata.UltimaVerificacion = AhoraChile();

            if (candidata.EstadoFlujo != EstadoFlujo.Pendiente)
            {
                tramoBajoActivas.Add(candidata);
                continue;
            }

            if (licitacion.CodigoEstado != 5)
            {
                var (resultadoApi, estadoTerminal) = Verificacion.ReverificacionService
                    .ClasificarEstadoApi(licitacion.CodigoEstado, candidata.Codigo);

                if (resultadoApi == Verificacion.ReverificacionService.ResultadoEstadoApi.Suspendida)
                {
                    tramoBajoActivas.Add(candidata);
                    continue;
                }

                candidata.EstadoFlujo = estadoTerminal!.Value;
                tramoBajoHistorico.Add(candidata);
                movidasHistoricoTramoBajo++;
                continue;
            }

            // Sigue Publicada: UNSPSC + rubro SIEMPRE, para cualquier
            // UnspscEstado resultante — a diferencia de Regular, acá
            // rubro_match/termino_match son el dato que se muestra en el
            // tablero, nunca deciden promoción de lista (Tramo bajo nunca
            // cambia de lista, con o sin rubro, con o sin UNSPSC).
            var entrada = cacheUnspsc[candidata.Codigo];
            var estadoUnspsc = ClasificadorUnspsc.Clasificar(entrada, catalogoUnspsc, familiasRevisionManual);
            var (rubro, termino, _) = FiltroLicitaciones.EvaluarRubro(
                criterios, TextoNormalizador.Normalizar(candidata.Nombre));
            candidata.UnspscEstado = estadoUnspsc;
            candidata.RubroMatch = rubro?.Id;
            candidata.TerminoMatch = termino;
            if (rubro is not null)
            {
                tramoBajoConRubro++;
            }

            tramoBajoActivas.Add(candidata);
        }

        cronometro.Stop();

        // Overrides humanos (ver AplicadorOverrides): una decisión ya
        // tomada desde el tablero debe seguir ganando aunque este modo
        // diga lo contrario — se aplica al final, sobre el resultado ya
        // reclasificado, antes de persistir.
        var secundariasExistentes = JsonStore.CargarOPredeterminado(
            rutaSecundarias, JsonOpciones.Persistencia, new List<Candidata>());
        var codigosSecundariasExistentes = secundariasExistentes.Select(c => c.Codigo).ToHashSet();
        foreach (var candidata in secundariasNuevas)
        {
            if (codigosSecundariasExistentes.Add(candidata.Codigo))
            {
                secundariasExistentes.Add(candidata);
            }
        }

        var overrides = JsonStore.CargarOPredeterminado(
            Path.Combine(repoRoot, "data", "overrides.json"), JsonOpciones.Persistencia,
            new Dictionary<string, EntradaOverride>());
        var (prioritariasFinal, secundariasFinal, _) = AplicadorOverrides.Aplicar(
            overrides, prioritariasActivas, secundariasExistentes);

        JsonStore.Guardar(rutaPrioritarias, prioritariasFinal, JsonOpciones.Persistencia);
        JsonStore.Guardar(rutaSecundarias, secundariasFinal, JsonOpciones.Persistencia);
        JsonStore.Guardar(rutaTramoBajo, tramoBajoActivas, JsonOpciones.Persistencia);

        if (prioritariasHistorico.Count > 0)
        {
            var historico = JsonStore.CargarOPredeterminado(rutaHistoricoPrioritarias, JsonOpciones.Persistencia, new List<Candidata>());
            historico.AddRange(prioritariasHistorico);
            JsonStore.Guardar(rutaHistoricoPrioritarias, historico, JsonOpciones.Persistencia);
        }

        if (tramoBajoHistorico.Count > 0)
        {
            var historico = JsonStore.CargarOPredeterminado(rutaHistoricoTramoBajo, JsonOpciones.Persistencia, new List<Candidata>());
            historico.AddRange(tramoBajoHistorico);
            JsonStore.Guardar(rutaHistoricoTramoBajo, historico, JsonOpciones.Persistencia);
        }

        // A diferencia del flujo diario (que solo agrega códigos nuevos y
        // por eso puede usar "¿creció el conteo?" como señal de cambio),
        // este modo SIEMPRE refresca entradas ya cacheadas — el conteo
        // puede quedar igual aunque el contenido haya cambiado. Se
        // reescribe sin condición si se procesó al menos un código.
        if (cacheModificado)
        {
            var cacheOrdenado = cacheUnspsc.Values.OrderBy(e => e.CodigoExterno, StringComparer.Ordinal).ToList();
            JsonStore.Guardar(rutaCacheUnspsc, cacheOrdenado, JsonOpciones.Persistencia);
        }

        var informesExistentes = CargarInformesConMigracion(Path.Combine(repoRoot, "data", "informes.json"));
        var dashboard = GeneradorDashboard.Construir(
            prioritariasFinal, secundariasFinal, tramoBajoActivas, informesExistentes, DateTime.UtcNow);
        JsonStore.Guardar(Path.Combine(repoRoot, "docs", "data.json"), dashboard, JsonOpciones.Persistencia);
        PublicarPanelRevisionConfig(repoRoot);

        var totalProcesadas = prioritarias.Count + tramoBajo.Count;
        var detalleEvento = $"total={totalProcesadas} historico_prioritarias={movidasHistoricoPrioritarias} " +
            $"historico_tramo_bajo={movidasHistoricoTramoBajo} tramo_bajo_con_rubro={tramoBajoConRubro} " +
            $"prioritarias_confirmadas={prioritariasConfirmadas} prioritarias_revision_degradada={prioritariasDegradadas}";
        var eventos = JsonStore.CargarOPredeterminado(
            Path.Combine(repoRoot, "data", "eventos.json"), JsonOpciones.Persistencia, new List<EventoAuditoria>());
        eventos.Add(new EventoAuditoria(DateTime.UtcNow, "sistema", "reevaluar_inventario", null, detalleEvento));
        JsonStore.Guardar(Path.Combine(repoRoot, "data", "eventos.json"), eventos, JsonOpciones.Persistencia);

        Console.WriteLine("== Resumen de --reevaluar-inventario ==");
        Console.WriteLine($"Procesadas: {totalProcesadas} (Prioritarias={prioritarias.Count}, Tramo bajo={tramoBajo.Count})");
        Console.WriteLine(
            $"Movidas a histórico por cierre: {movidasHistoricoPrioritarias + movidasHistoricoTramoBajo} " +
            $"(Prioritarias={movidasHistoricoPrioritarias}, Tramo bajo={movidasHistoricoTramoBajo})");
        Console.WriteLine($"Tramo bajo con rubro_match poblado: {tramoBajoConRubro}");
        Console.WriteLine($"Prioritarias confirmadas (sin cambios): {prioritariasConfirmadas}");
        Console.WriteLine($"Prioritarias a RevisionDegradada: {prioritariasDegradadas}");
        Console.WriteLine(
            $"Enriquecimiento: llamadas_intentadas={llamadasIntentadas} exitosas={llamadasExitosas} " +
            $"fallidas={llamadasIntentadas - llamadasExitosas} tiempo_total={cronometro.Elapsed.TotalSeconds:F1}s " +
            (llamadasIntentadas > 0
                ? $"promedio={cronometro.Elapsed.TotalSeconds / llamadasIntentadas:F2}s/llamada"
                : "(nada que procesar)"));

        return 0;
    }

    // Requiere MP_TICKET real — no tiene equivalente a --fixture (consultar
    // un código puntual no tiene sentido offline).
    //
    // A diferencia de --backfill-unspsc/--reevaluar-inventario, este modo no
    // toca candidatas.json/secundarias.json/tramo_bajo.json/eventos.json —
    // es una consulta puntual, sin efecto sobre el estado del pipeline. Su
    // única salida es data/consultas/{codigo}.json, que además sirve de
    // cache: si el tablero vuelve a pedir el mismo código, lo lee directo
    // sin disparar este modo de nuevo (ver diseño de la pestaña "Consulta").
    private static async Task<int> EjecutarConsultarLicitacionAsync(string repoRoot, string codigo)
    {
        var ticket = Environment.GetEnvironmentVariable("MP_TICKET")
            ?? throw new MercadoPublicoApiException(
                "Falta la variable de entorno MP_TICKET (--consultar-licitacion requiere red real, no tiene modo --fixture).");

        using var http = new HttpClient();
        var cliente = new MercadoPublicoClient(http, ticket);
        var respuesta = await cliente.ObtenerDetalleAsync(codigo);
        var detalle = respuesta.Listado.FirstOrDefault(l => l.CodigoExterno == codigo);

        var resultado = new ResultadoConsulta(codigo, DateTime.UtcNow, detalle is not null, detalle);

        // Saneo defensivo del nombre de archivo: los códigos reales usan
        // '-' (ej. 734-50-LE26), válido en cualquier filesystem, así que
        // esto solo importa si el tablero envía un código con caracteres
        // inválidos (espacio, '/', etc.) — nunca debería pasar con un
        // código real, pero el input viene de un campo de texto libre.
        var codigoSaneado = string.Concat(codigo.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var rutaSalida = Path.Combine(repoRoot, "data", "consultas", $"{codigoSaneado}.json");
        JsonStore.Guardar(rutaSalida, resultado, JsonOpciones.Persistencia);

        if (!resultado.Encontrado)
        {
            Console.WriteLine($"[CONSULTA] {codigo}: no encontrado (Listado vacío en la respuesta de la API).");
            return 0;
        }

        Console.WriteLine($"[CONSULTA] {codigo}: encontrado. CodigoEstado={detalle!.CodigoEstado}");
        Console.WriteLine(
            "[CONSULTA] Adjudicacion: " +
            (detalle.Adjudicacion is null
                ? "null (licitación no adjudicada todavía)"
                : $"Numero={detalle.Adjudicacion.Numero} Fecha={detalle.Adjudicacion.Fecha} " +
                  $"NumeroOferentes={detalle.Adjudicacion.NumeroOferentes}"));

        return 0;
    }

    // Requiere MP_TICKET real — no tiene equivalente a --fixture.
    //
    // A diferencia de --backfill-unspsc/--reevaluar-inventario, este modo
    // NUNCA reclasifica ni mueve de lista — nunca llama a
    // ClasificadorUnspsc.Clasificar ni a EvaluarRubro/EsRegionElegible, y
    // nunca toca UnspscEstado/RubroMatch/TerminoMatch/EstadoFlujo. Su único
    // trabajo es refrescar campos descriptivos (Region, Moneda, Monto,
    // CantidadReclamos, ItemsUnspsc, Organismo, Comuna, Descripcion,
    // ProhibicionContratacion, TipoPago, SubContratacion) sobre Prioritarias
    // completas (sin el filtro de acumulados del flujo diario — acá es
    // deliberado, mismo criterio que --backfill-unspsc/--reevaluar-inventario
    // ya usan para procesar confirmadas) y la cola de Revisión dentro de
    // Secundarias (mismo filtro que ya usa docs/app.js:
    // unspsc_estado=revision_manual o estado_flujo en
    // {revision_ambigua, revision_degradada}) — el resto de Secundarias
    // (~3.700 códigos que nunca entraron a esa cola) queda fuera de
    // alcance, sin cambios. Decisión explícita del usuario tras el
    // hallazgo de que ningún modo tocaba Secundarias y el panel expandible
    // de Revisión quedaba con campos vacíos para candidatas ya persistidas
    // antes de que estos campos existieran en el modelo.
    private static async Task<int> EjecutarRefrescarDescriptivosAsync(string repoRoot)
    {
        var ticket = Environment.GetEnvironmentVariable("MP_TICKET")
            ?? throw new MercadoPublicoApiException(
                "Falta la variable de entorno MP_TICKET (--refrescar-descriptivos requiere red real, no tiene modo --fixture).");

        var rutaCacheUnspsc = Path.Combine(repoRoot, "data", "cache-unspsc.json");
        var cacheUnspscInicial = JsonStore.CargarOPredeterminado(
            rutaCacheUnspsc, JsonOpciones.Persistencia, new List<EntradaCacheUnspsc>());
        var cacheUnspsc = cacheUnspscInicial.ToDictionary(e => e.CodigoExterno);

        var rutaPrioritarias = Path.Combine(repoRoot, "data", "candidatas.json");
        var prioritarias = JsonStore.CargarOPredeterminado(
            rutaPrioritarias, JsonOpciones.Persistencia, new List<Candidata>());

        var rutaSecundarias = Path.Combine(repoRoot, "data", "secundarias.json");
        var secundarias = JsonStore.CargarOPredeterminado(
            rutaSecundarias, JsonOpciones.Persistencia, new List<Candidata>());
        var colaRevision = secundarias
            .Where(c => c.UnspscEstado == UnspscEstado.RevisionManual
                || c.EstadoFlujo == EstadoFlujo.RevisionAmbigua
                || c.EstadoFlujo == EstadoFlujo.RevisionDegradada)
            .ToList();

        var candidatas = prioritarias.Concat(colaRevision).ToList();

        using var http = new HttpClient();
        var cliente = new MercadoPublicoClient(http, ticket);

        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var llamadasIntentadas = 0;
        var llamadasExitosas = 0;

        foreach (var candidata in candidatas)
        {
            llamadasIntentadas++;
            DetalleLicitacion? detalle;
            try
            {
                var respuesta = await cliente.ObtenerDetalleAsync(candidata.Codigo);
                detalle = respuesta.Listado.FirstOrDefault(l => l.CodigoExterno == candidata.Codigo);
            }
            catch (MercadoPublicoApiException ex)
            {
                Console.Error.WriteLine(
                    $"[ADVERTENCIA] Refresco omitido para {candidata.Codigo}, se reintenta la próxima corrida: {ex.Message}");
                continue;
            }

            llamadasExitosas++;

            // Siempre refresca la entrada de cache (no solo cache-miss, a
            // diferencia del flujo diario) — mismo criterio que
            // --reevaluar-inventario: el propósito de este modo es
            // justamente traer datos más frescos que los ya cacheados.
            var entrada = EnriquecimientoUnspscService.ConstruirEntrada(candidata.Codigo, detalle);
            cacheUnspsc[candidata.Codigo] = entrada;

            candidata.Region = entrada.RegionUnidad;
            candidata.Moneda = entrada.Moneda;
            candidata.Monto = entrada.Monto;
            candidata.CantidadReclamos = entrada.CantidadReclamos;
            candidata.Organismo = entrada.NombreOrganismo;
            candidata.Comuna = entrada.ComunaUnidad;
            candidata.Descripcion = entrada.Descripcion;
            candidata.ProhibicionContratacion = entrada.ProhibicionContratacion;
            candidata.TipoPago = entrada.TipoPago;
            candidata.SubContratacion = entrada.SubContratacion;
            candidata.CodigosProductoUnspsc = entrada.Items
                .Where(i => i.CodigoProducto is not null)
                .Select(i => i.CodigoProducto!.Value)
                .ToList();
            candidata.ItemsUnspsc = entrada.Items;
            // FechaCierre del detalle es más fresco que el del listado
            // original (ver DetalleLicitacionResponse.cs) — se usa para
            // refrescar solo cuando viene poblado, nunca para borrar un
            // valor ya conocido con uno ausente.
            if (entrada.FechaCierre is not null)
            {
                candidata.FechaCierre = entrada.FechaCierre;
            }
        }
        cronometro.Stop();

        JsonStore.Guardar(rutaPrioritarias, prioritarias, JsonOpciones.Persistencia);
        JsonStore.Guardar(rutaSecundarias, secundarias, JsonOpciones.Persistencia);

        if (llamadasExitosas > 0)
        {
            var cacheOrdenado = cacheUnspsc.Values.OrderBy(e => e.CodigoExterno, StringComparer.Ordinal).ToList();
            JsonStore.Guardar(rutaCacheUnspsc, cacheOrdenado, JsonOpciones.Persistencia);
        }

        var tramoBajo = JsonStore.CargarOPredeterminado(
            Path.Combine(repoRoot, "data", "tramo_bajo.json"), JsonOpciones.Persistencia, new List<Candidata>());
        var informesExistentes = CargarInformesConMigracion(Path.Combine(repoRoot, "data", "informes.json"));
        var dashboard = GeneradorDashboard.Construir(prioritarias, secundarias, tramoBajo, informesExistentes, DateTime.UtcNow);
        JsonStore.Guardar(Path.Combine(repoRoot, "docs", "data.json"), dashboard, JsonOpciones.Persistencia);
        PublicarPanelRevisionConfig(repoRoot);

        var detalleEvento = $"total={candidatas.Count} prioritarias={prioritarias.Count} " +
            $"cola_revision={colaRevision.Count} llamadas_exitosas={llamadasExitosas} " +
            $"llamadas_fallidas={llamadasIntentadas - llamadasExitosas}";
        var eventos = JsonStore.CargarOPredeterminado(
            Path.Combine(repoRoot, "data", "eventos.json"), JsonOpciones.Persistencia, new List<EventoAuditoria>());
        eventos.Add(new EventoAuditoria(DateTime.UtcNow, "sistema", "refrescar_descriptivos", null, detalleEvento));
        JsonStore.Guardar(Path.Combine(repoRoot, "data", "eventos.json"), eventos, JsonOpciones.Persistencia);

        Console.WriteLine("== Resumen de --refrescar-descriptivos ==");
        Console.WriteLine(
            $"Procesadas: {candidatas.Count} (Prioritarias={prioritarias.Count}, cola de Revisión={colaRevision.Count})");
        Console.WriteLine(
            $"Llamadas: intentadas={llamadasIntentadas} exitosas={llamadasExitosas} " +
            $"fallidas={llamadasIntentadas - llamadasExitosas} tiempo_total={cronometro.Elapsed.TotalSeconds:F1}s " +
            (llamadasIntentadas > 0
                ? $"promedio={cronometro.Elapsed.TotalSeconds / llamadasIntentadas:F2}s/llamada"
                : "(nada que procesar)"));

        return 0;
    }

    // Modo manual (2026-09-30, mismo patrón estructural que los otros
    // cuatro): limpia el backlog completo de Secundarias + Tramo bajo
    // re-verificando contra el detalle real de la API TODAS las Pendiente
    // vencidas (o sin fecha conocida), sin el tope diario del paso
    // automático ni el salto de "ya verificada hoy" (ver
    // Verificacion.ReverificacionService.SeleccionarVencidasSinTope vs
    // SeleccionarTope) — es un vaciado de backlog de una sola vez, no la
    // corrida incremental diaria: correr este modo dos veces el mismo día
    // debe seguir procesando lo que quede pendiente. Prioritarias no lo
    // necesita: el flujo automático ya la re-verifica completa cada noche.
    //
    // Requiere MP_TICKET real — no tiene equivalente a --fixture. Nunca se
    // dispara automáticamente: solo con confirmación explícita del usuario
    // (cuota de API compartida con enriquecimiento/--backfill-unspsc/
    // --reevaluar-inventario — el usuario señala un límite de 10.000
    // solicitudes/día en el ticket de Mercado Público).
    //
    // No toca informes.json (mismo criterio que --backfill-unspsc/
    // --reevaluar-inventario/--refrescar-descriptivos) — sí escribe a
    // eventos.json y regenera docs/data.json para que el tablero refleje el
    // backlog ya limpio.
    private static async Task<int> EjecutarReverificarVencidasAsync(string repoRoot)
    {
        var ticket = Environment.GetEnvironmentVariable("MP_TICKET")
            ?? throw new MercadoPublicoApiException(
                "Falta la variable de entorno MP_TICKET (--reverificar-vencidas requiere red real, no tiene modo --fixture).");

        var rutaSecundarias = Path.Combine(repoRoot, "data", "secundarias.json");
        var rutaTramoBajo = Path.Combine(repoRoot, "data", "tramo_bajo.json");
        var rutaHistoricoSecundarias = Path.Combine(repoRoot, "data", "historico", "secundarias.json");
        var rutaHistoricoTramoBajo = Path.Combine(repoRoot, "data", "historico", "tramo_bajo.json");

        var secundarias = JsonStore.CargarOPredeterminado(rutaSecundarias, JsonOpciones.Persistencia, new List<Candidata>());
        var tramoBajo = JsonStore.CargarOPredeterminado(rutaTramoBajo, JsonOpciones.Persistencia, new List<Candidata>());

        using var http = new HttpClient();
        var cliente = new MercadoPublicoClient(http, ticket);

        async Task<DetalleLicitacion?> ObtenerDetalle(string codigo, CancellationToken ct)
        {
            var respuesta = await cliente.ObtenerDetalleAsync(codigo, ct);
            return respuesta.Listado.FirstOrDefault(l => l.CodigoExterno == codigo);
        }

        // Mismo motivo que el paso automático (ver Main): FechaCierre es
        // hora de Chile sin zona — se usa AhoraChile(), nunca DateTime.UtcNow.
        var ahora = AhoraChile();
        var seleccionadas = Verificacion.ReverificacionService.SeleccionarVencidasSinTope(secundarias, tramoBajo, ahora);

        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var resultado = await Verificacion.ReverificacionService.ReverificarCandidatasAsync(
            ObtenerDetalle, seleccionadas, new HashSet<EstadoFlujo>(), ahora);
        cronometro.Stop();

        var terminalSecundarias = resultado.PasaronATerminal.Where(c => secundarias.Contains(c)).ToList();
        var terminalTramoBajo = resultado.PasaronATerminal.Where(c => tramoBajo.Contains(c)).ToList();

        if (terminalSecundarias.Count > 0)
        {
            secundarias = secundarias.Except(terminalSecundarias).ToList();
            var historico = JsonStore.CargarOPredeterminado(rutaHistoricoSecundarias, JsonOpciones.Persistencia, new List<Candidata>());
            historico.AddRange(terminalSecundarias);
            JsonStore.Guardar(rutaHistoricoSecundarias, historico, JsonOpciones.Persistencia);
        }

        if (terminalTramoBajo.Count > 0)
        {
            tramoBajo = tramoBajo.Except(terminalTramoBajo).ToList();
            var historico = JsonStore.CargarOPredeterminado(rutaHistoricoTramoBajo, JsonOpciones.Persistencia, new List<Candidata>());
            historico.AddRange(terminalTramoBajo);
            JsonStore.Guardar(rutaHistoricoTramoBajo, historico, JsonOpciones.Persistencia);
        }

        JsonStore.Guardar(rutaSecundarias, secundarias, JsonOpciones.Persistencia);
        JsonStore.Guardar(rutaTramoBajo, tramoBajo, JsonOpciones.Persistencia);

        if (resultado.Eventos.Count > 0)
        {
            var rutaEventos = Path.Combine(repoRoot, "data", "eventos.json");
            var eventos = JsonStore.CargarOPredeterminado(rutaEventos, JsonOpciones.Persistencia, new List<EventoAuditoria>());
            eventos.AddRange(resultado.Eventos);
            JsonStore.Guardar(rutaEventos, eventos, JsonOpciones.Persistencia);
        }

        var prioritariasActuales = JsonStore.CargarOPredeterminado(
            Path.Combine(repoRoot, "data", "candidatas.json"), JsonOpciones.Persistencia, new List<Candidata>());
        var informesExistentes = CargarInformesConMigracion(Path.Combine(repoRoot, "data", "informes.json"));
        var dashboard = GeneradorDashboard.Construir(prioritariasActuales, secundarias, tramoBajo, informesExistentes, DateTime.UtcNow);
        JsonStore.Guardar(Path.Combine(repoRoot, "docs", "data.json"), dashboard, JsonOpciones.Persistencia);
        PublicarPanelRevisionConfig(repoRoot);

        // Histograma de CodigoEstado observados (pedido explícito para la
        // primera corrida, ver el follow-up que motivó este modo): incluye
        // cualquier valor no documentado más allá de 5/6/7/8/18/19 —
        // evidencia real para cerrar cualquier duda sobre valores no
        // contemplados en el diseño. Solo cuenta candidatas encontradas
        // (EstadoMp poblado); las que nunca se encontraron no aportan un
        // CodigoEstado real.
        var histograma = seleccionadas
            .Where(c => c.EstadoMp.HasValue)
            .GroupBy(c => c.EstadoMp!.Value)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        var publicadaPeroVencida = seleccionadas.Count(c =>
            c.EstadoMp == 5 && c.FechaCierre is not null && c.FechaCierre.Value < ahora);

        Console.WriteLine("== Resumen de --reverificar-vencidas ==");
        Console.WriteLine($"Seleccionadas (Pendiente vencidas o sin fecha, Secundarias+Tramo bajo, sin tope): {seleccionadas.Count}");
        Console.WriteLine(
            $"Llamadas: verificadas={resultado.Verificadas} fallidas={resultado.Fallidas} " +
            $"tiempo_total={cronometro.Elapsed.TotalSeconds:F1}s " +
            (seleccionadas.Count > 0
                ? $"promedio={cronometro.Elapsed.TotalSeconds / seleccionadas.Count:F2}s/llamada"
                : "(nada que procesar)"));
        Console.WriteLine(
            $"Reverificación: cambios_fecha={resultado.CambiosFecha} " +
            $"a_terminal={terminalSecundarias.Count + terminalTramoBajo.Count} " +
            $"(secundarias={terminalSecundarias.Count}, tramo_bajo={terminalTramoBajo.Count}) " +
            $"cierres_detectados_en_triage={resultado.CierresDetectadosEnTriage} " +
            $"publicada_pero_vencida={publicadaPeroVencida}");
        Console.WriteLine(
            "Histograma CodigoEstado observado: " +
            (histograma.Count == 0
                ? "(ninguno)"
                : string.Join(", ", histograma.Select(kv => $"{kv.Key}={kv.Value}"))));

        return 0;
    }
}
