using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScorePlusTwo.Pipeline.Api;
using ScorePlusTwo.Pipeline.Cli;
using ScorePlusTwo.Pipeline.Dashboard;
using ScorePlusTwo.Pipeline.Filtro;
using ScorePlusTwo.Pipeline.Infraestructura;
using ScorePlusTwo.Pipeline.Modelos;
using ScorePlusTwo.Pipeline.Persistencia;
using ScorePlusTwo.Pipeline.Refiltrado;

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

            List<LicitacionRaw> loteDiario;
            DateOnly fecha;
            ResultadoFiltro? resultadoActivas = null;
            string estadoActivas;

            if (opciones.RutaFixture is not null)
            {
                // Modo local: sin red, sin MP_TICKET, sin barrido activas.
                fecha = opciones.Fecha ?? DateOnly.FromDateTime(AhoraChile()).AddDays(-1);
                estadoActivas = "omitido (modo --fixture, sin red)";
                var rutaFixture = Path.IsPathRooted(opciones.RutaFixture)
                    ? opciones.RutaFixture
                    : Path.Combine(Directory.GetCurrentDirectory(), opciones.RutaFixture);

                var respuestaFixture = JsonStore.Cargar<ListadoLicitacionesResponse>(rutaFixture, JsonOpciones.ApiLectura);
                loteDiario = respuestaFixture.Listado;
                GuardarRawDelDia(repoRoot, fecha, respuestaFixture);
            }
            else
            {
                var ticket = Environment.GetEnvironmentVariable("MP_TICKET")
                    ?? throw new MercadoPublicoApiException("Falta la variable de entorno MP_TICKET.");
                fecha = opciones.Fecha ?? DateOnly.FromDateTime(AhoraChile()).AddDays(-1);

                using var http = new HttpClient();
                var cliente = new MercadoPublicoClient(http, ticket);

                // Diario + adjudicada: fatal si cualquiera falla, nada se persiste ese día.
                var respuestaDiaria = await cliente.ObtenerListadoDiarioAsync(fecha);
                var respuestaAdjudicada = await cliente.ObtenerAdjudicadasAsync(fecha);

                GuardarRawDelDia(repoRoot, fecha, respuestaDiaria);
                AcumularAdjudicadas(repoRoot, fecha, respuestaAdjudicada.Listado);
                loteDiario = respuestaDiaria.Listado;

                var (corresponde, motivoActivas) = DecidirBarridoActivas(repoRoot);
                if (corresponde)
                {
                    // Asimetría deliberada: un fallo aquí NUNCA es fatal para el resto del pipeline.
                    try
                    {
                        var respuestaActivas = await cliente.ObtenerActivasAsync();
                        GuardarRawActivas(repoRoot, DateOnly.FromDateTime(AhoraChile()), respuestaActivas);

                        var criteriosParaActivas = JsonStore.Cargar<Criterios>(
                            Path.Combine(repoRoot, "config", "criterios.json"), JsonOpciones.Config);
                        resultadoActivas = FiltroLicitaciones.Filtrar(respuestaActivas.Listado, criteriosParaActivas);
                        estadoActivas = $"corrió ({motivoActivas})";
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
            }

            var criterios = JsonStore.Cargar<Criterios>(
                Path.Combine(repoRoot, "config", "criterios.json"), JsonOpciones.Config);

            var resultadoDiario = FiltroLicitaciones.Filtrar(loteDiario, criterios);
            var fechaActivas = DateOnly.FromDateTime(AhoraChile());

            // Tres listas, tres archivos — mismo patrón de merge/dedupe para
            // cada una (ver FusionarLista): Prioritarias sigue siendo
            // candidatas.json; Secundarias y TramoBajo son nuevos, con la
            // misma lógica de "activas rescata lo que el diario no habría
            // detectado" aplicada a las tres por igual.
            var (todasPrioritarias, nuevasPrioritariasDiario, nuevasPrioritariasActivas) = FusionarLista(
                Path.Combine(repoRoot, "data", "candidatas.json"),
                resultadoDiario.Prioritarias, resultadoActivas?.Prioritarias,
                fecha, fechaActivas, tramo: null);

            var (todasSecundarias, nuevasSecundariasDiario, nuevasSecundariasActivas) = FusionarLista(
                Path.Combine(repoRoot, "data", "secundarias.json"),
                resultadoDiario.Secundarias, resultadoActivas?.Secundarias,
                fecha, fechaActivas, tramo: null);

            var (todasTramoBajo, nuevasTramoBajoDiario, nuevasTramoBajoActivas) = FusionarLista(
                Path.Combine(repoRoot, "data", "tramo_bajo.json"),
                resultadoDiario.TramoBajo, resultadoActivas?.TramoBajo,
                fecha, fechaActivas, tramo: "bajo");

            // El filtro de estado solo se aplica al capturar — de ahí en
            // adelante las listas nunca vuelven a consultar el estado y
            // acumularían licitaciones cerradas para siempre. El lote diario
            // crudo (antes de filtrar por estado) ya trae esas transiciones
            // gratis: si un código de una lista aparece hoy con estado
            // distinto de Publicada, se mueve a data/historico/ — salvo que
            // ya tenga triage humano encima, que se respeta tal cual.
            var (prioritariasActivas, prioritariasMovidas) = RevalidarEstado(
                Path.Combine(repoRoot, "data", "candidatas.json"),
                Path.Combine(repoRoot, "data", "historico", "candidatas.json"),
                todasPrioritarias, loteDiario);

            var (secundariasActivas, secundariasMovidas) = RevalidarEstado(
                Path.Combine(repoRoot, "data", "secundarias.json"),
                Path.Combine(repoRoot, "data", "historico", "secundarias.json"),
                todasSecundarias, loteDiario);

            var (tramoBajoActivas, tramoBajoMovidas) = RevalidarEstado(
                Path.Combine(repoRoot, "data", "tramo_bajo.json"),
                Path.Combine(repoRoot, "data", "historico", "tramo_bajo.json"),
                todasTramoBajo, loteDiario);

            var totalMovidasHistorico = prioritariasMovidas + secundariasMovidas + tramoBajoMovidas;

            // NOTA (sin arreglar todavía): barridoActivasFunnel queda indexado
            // bajo `fecha` — que es el día del LOTE DIARIO (ayer), no el día
            // en que corrió el barrido `activas` (hoy). Ej.: el barrido del
            // lunes 07-09 quedó registrado en el informe del 06-09. Mientras
            // nadie necesite correlacionar el barrido con el día de la
            // semana real en que corrió, no afecta nada — pero el día está
            // corrido en uno y hay que corregirlo antes de usar esta fecha
            // para ese análisis.
            var barridoActivasFunnel = resultadoActivas is null
                ? null
                : new InformeFunnel(
                    resultadoActivas.Total, resultadoActivas.TrasEstado, resultadoActivas.TrasTipo,
                    resultadoActivas.TrasRegion, resultadoActivas.DescarteDuro,
                    resultadoActivas.Prioritarias.Count, resultadoActivas.Secundarias.Count, resultadoActivas.TramoBajo.Count,
                    nuevasPrioritariasActivas.Count, nuevasSecundariasActivas.Count, nuevasTramoBajoActivas.Count);

            var informeHoy = new InformeDiario(
                fecha,
                resultadoDiario.Total,
                resultadoDiario.TrasEstado,
                resultadoDiario.TrasTipo,
                resultadoDiario.TrasRegion,
                resultadoDiario.DescarteDuro,
                resultadoDiario.Prioritarias.Count,
                resultadoDiario.Secundarias.Count,
                resultadoDiario.TramoBajo.Count,
                nuevasPrioritariasDiario.Count,
                nuevasSecundariasDiario.Count,
                nuevasTramoBajoDiario.Count,
                barridoActivasFunnel);

            var informes = ActualizarSerieInformes(repoRoot, informeHoy);
            JsonStore.Guardar(Path.Combine(repoRoot, "data", "informes.json"), informes, JsonOpciones.Persistencia);

            var totalNuevasHoy = nuevasPrioritariasDiario.Count + nuevasPrioritariasActivas.Count
                + nuevasSecundariasDiario.Count + nuevasSecundariasActivas.Count
                + nuevasTramoBajoDiario.Count + nuevasTramoBajoActivas.Count;
            RegistrarEvento(repoRoot, informeHoy, totalNuevasHoy, totalMovidasHistorico);

            var dashboard = GeneradorDashboard.Construir(prioritariasActivas, secundariasActivas, tramoBajoActivas, informes, DateTime.UtcNow);
            JsonStore.Guardar(Path.Combine(repoRoot, "docs", "data.json"), dashboard, JsonOpciones.Persistencia);

            ImprimirResumen(resultadoDiario, nuevasPrioritariasDiario.Count, nuevasSecundariasDiario.Count, nuevasTramoBajoDiario.Count, estadoActivas, resultadoActivas);

            return 0;
        }
        catch (MercadoPublicoApiException ex)
        {
            Console.Error.WriteLine($"[FATAL] Pipeline abortado sin escribir cambios: {ex.Message}");
            return 1;
        }
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

    // Se intenta si es la primera corrida real (aún no existe ningún
    // data/raw/activas-*.json, siembra inicial) o si hoy es lunes en
    // huso horario de Chile (no UTC: el cron corre de madrugada en Chile,
    // pero sigue siendo hora de Chile la que decide qué día es, no la del
    // runner — ver AhoraChile()).
    private static (bool Corresponde, string Motivo) DecidirBarridoActivas(string repoRoot)
    {
        var directorioRaw = Path.Combine(repoRoot, "data", "raw");
        var yaHuboActivas = Directory.Exists(directorioRaw)
            && Directory.EnumerateFiles(directorioRaw, "activas-*.json").Any();

        if (!yaHuboActivas)
        {
            return (true, "primera corrida, siembra inicial");
        }

        var ahoraChile = AhoraChile();
        return ahoraChile.DayOfWeek == DayOfWeek.Monday
            ? (true, "lunes en Chile")
            : (false, $"hoy es {ahoraChile.DayOfWeek} en Chile, no lunes");
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

    private static Candidata CrearCandidata(CandidataDetectada detectada, DateOnly fechaLote, OrigenCandidata origen, string? tramo) =>
        new()
        {
            Codigo = detectada.Origen.CodigoExterno,
            Nombre = detectada.Origen.Nombre,
            Tipo = detectada.Tipo,
            FechaCierre = detectada.Origen.FechaCierre,
            FechaLote = fechaLote,
            RubroMatch = detectada.RubroMatch,
            TerminoMatch = detectada.TerminoMatch,
            Region = null,
            Organismo = null,
            EstadoFlujo = EstadoFlujo.Pendiente,
            Notas = null,
            ClienteAsignado = null,
            Origen = origen,
            Tramo = tramo,
        };

    // Mismo merge/dedupe para las tres listas (Prioritarias -> candidatas.json,
    // Secundarias -> secundarias.json, TramoBajo -> tramo_bajo.json): lo
    // detectado en el barrido `activas` que ya esté en el archivo existente o
    // ya haya salido del lote diario de hoy no se duplica — es lo que permite
    // medir después cuántas se habrían perdido sin el barrido, generalizado a
    // las tres listas por igual.
    private static (List<Candidata> Todas, List<Candidata> NuevasDiario, List<Candidata> NuevasActivas) FusionarLista(
        string rutaArchivo,
        IReadOnlyList<CandidataDetectada> detectadasDiario,
        IReadOnlyList<CandidataDetectada>? detectadasActivas,
        DateOnly fechaDiario,
        DateOnly fechaActivas,
        string? tramo)
    {
        var existentes = JsonStore.CargarOPredeterminado(rutaArchivo, JsonOpciones.Persistencia, new List<Candidata>());
        var codigosExistentes = existentes.Select(c => c.Codigo).ToHashSet();
        var codigosDiario = detectadasDiario.Select(c => c.Origen.CodigoExterno).ToHashSet();

        var nuevasDiario = detectadasDiario
            .Where(c => !codigosExistentes.Contains(c.Origen.CodigoExterno))
            .Select(c => CrearCandidata(c, fechaDiario, OrigenCandidata.Diario, tramo))
            .ToList();

        var nuevasActivas = detectadasActivas is null
            ? new List<Candidata>()
            : detectadasActivas
                .Where(c => !codigosExistentes.Contains(c.Origen.CodigoExterno) && !codigosDiario.Contains(c.Origen.CodigoExterno))
                .Select(c => CrearCandidata(c, fechaActivas, OrigenCandidata.Activas, tramo))
                .ToList();

        var todas = existentes.Concat(nuevasDiario).Concat(nuevasActivas).ToList();
        JsonStore.Guardar(rutaArchivo, todas, JsonOpciones.Persistencia);

        return (todas, nuevasDiario, nuevasActivas);
    }

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
        var informeAGuardar = existente is null
            ? informeHoy
            : informeHoy with
            {
                NuevasPrioritarias = existente.NuevasPrioritarias,
                NuevasSecundarias = existente.NuevasSecundarias,
                NuevasTramoBajo = existente.NuevasTramoBajo,
                BarridoActivas = informeHoy.BarridoActivas is null
                    ? existente.BarridoActivas
                    : existente.BarridoActivas is null
                        ? informeHoy.BarridoActivas
                        : informeHoy.BarridoActivas with
                        {
                            NuevasPrioritarias = existente.BarridoActivas.NuevasPrioritarias,
                            NuevasSecundarias = existente.BarridoActivas.NuevasSecundarias,
                            NuevasTramoBajo = existente.BarridoActivas.NuevasTramoBajo,
                        },
            };

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

    private static EstadoFlujo? MapearEstadoDeCierre(int codigoEstado) => codigoEstado switch
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
    private static void ImprimirResumen(
        ResultadoFiltro resultadoDiario, int nuevasPrioritariasDiario, int nuevasSecundariasDiario, int nuevasTramoBajoDiario,
        string estadoActivas, ResultadoFiltro? resultadoActivas)
    {
        Console.WriteLine("== Resumen de la corrida ==");
        Console.WriteLine(
            $"Lote diario: total={resultadoDiario.Total} tras_estado={resultadoDiario.TrasEstado} " +
            $"tras_tipo={resultadoDiario.TrasTipo} descarte_duro={resultadoDiario.DescarteDuro} " +
            $"prioritarias={resultadoDiario.Prioritarias.Count} (nuevas={nuevasPrioritariasDiario}) " +
            $"secundarias={resultadoDiario.Secundarias.Count} (nuevas={nuevasSecundariasDiario}) " +
            $"tramo_bajo={resultadoDiario.TramoBajo.Count} (nuevas={nuevasTramoBajoDiario})");

        Console.WriteLine(resultadoActivas is null
            ? $"Barrido 'activas': {estadoActivas}"
            : $"Barrido 'activas': {estadoActivas} -> total={resultadoActivas.Total} " +
              $"tras_estado={resultadoActivas.TrasEstado} tras_tipo={resultadoActivas.TrasTipo} " +
              $"descarte_duro={resultadoActivas.DescarteDuro} prioritarias={resultadoActivas.Prioritarias.Count} " +
              $"secundarias={resultadoActivas.Secundarias.Count} tramo_bajo={resultadoActivas.TramoBajo.Count}");
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

        var directorioRaw = Path.Combine(repoRoot, "data", "raw");
        var resultado = RefiltradoService.Ejecutar(directorioRaw, criterios, opciones.Desde);

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
}
