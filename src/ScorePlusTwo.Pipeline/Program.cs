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

            List<LicitacionRaw> loteDiario;
            DateOnly fecha;
            ResultadoFiltro resultadoDiario;
            int enriquecidosDiarioHoy;
            int enriquecimientosFallidosDiarioHoy;
            int ahorradosPorAcumuladosDiario;
            ResultadoFiltro? resultadoActivas = null;
            int enriquecidosActivasHoy = 0;
            int enriquecimientosFallidosActivasHoy = 0;
            int ahorradosPorAcumuladosActivas = 0;
            string estadoActivas;

            if (opciones.RutaFixture is not null)
            {
                // Modo local: sin red, sin MP_TICKET, sin barrido activas, sin
                // enriquecimiento nuevo — clasifica con lo que ya haya en el
                // cache de disco (típicamente nada, en una copia fresca).
                fecha = opciones.Fecha ?? DateOnly.FromDateTime(AhoraChile()).AddDays(-1);
                estadoActivas = "omitido (modo --fixture, sin red)";
                var rutaFixture = Path.IsPathRooted(opciones.RutaFixture)
                    ? opciones.RutaFixture
                    : Path.Combine(Directory.GetCurrentDirectory(), opciones.RutaFixture);

                var respuestaFixture = JsonStore.Cargar<ListadoLicitacionesResponse>(rutaFixture, JsonOpciones.ApiLectura);
                loteDiario = respuestaFixture.Listado;
                GuardarRawDelDia(repoRoot, fecha, respuestaFixture);

                (resultadoDiario, enriquecidosDiarioHoy, enriquecimientosFallidosDiarioHoy, ahorradosPorAcumuladosDiario) =
                    await FiltrarConEnriquecimientoAsync(loteDiario, criterios, cacheUnspsc, catalogoUnspsc, codigosConfirmados, cliente: null);
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

                (resultadoDiario, enriquecidosDiarioHoy, enriquecimientosFallidosDiarioHoy, ahorradosPorAcumuladosDiario) =
                    await FiltrarConEnriquecimientoAsync(loteDiario, criterios, cacheUnspsc, catalogoUnspsc, codigosConfirmados, cliente);

                var (corresponde, motivoActivas) = DecidirBarridoActivas(repoRoot);
                if (corresponde)
                {
                    // Asimetría deliberada: un fallo aquí NUNCA es fatal para el resto del pipeline.
                    try
                    {
                        var respuestaActivas = await cliente.ObtenerActivasAsync();
                        GuardarRawActivas(repoRoot, DateOnly.FromDateTime(AhoraChile()), respuestaActivas);

                        (resultadoActivas, enriquecidosActivasHoy, enriquecimientosFallidosActivasHoy, ahorradosPorAcumuladosActivas) =
                            await FiltrarConEnriquecimientoAsync(respuestaActivas.Listado, criterios, cacheUnspsc, catalogoUnspsc, codigosConfirmados, cliente);
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

            // El cache solo crece (nunca se invalida, ver EntradaCacheUnspsc):
            // se reescribe solo si el enriquecimiento de hoy agregó algo,
            // mismo criterio que RevalidarEstado usa para no ensuciar el
            // historial de git sin cambios reales.
            if (cacheUnspsc.Count != cacheUnspscCountInicial)
            {
                var cacheOrdenado = cacheUnspsc.Values
                    .OrderBy(e => e.CodigoExterno, StringComparer.Ordinal)
                    .ToList();
                JsonStore.Guardar(rutaCacheUnspsc, cacheOrdenado, JsonOpciones.Persistencia);
            }

            var fechaActivas = DateOnly.FromDateTime(AhoraChile());
            var tiposPrivados = criterios.TiposPrivados.ToHashSet();

            // Tres listas, tres archivos — mismo patrón de merge/dedupe para
            // cada una (ver FusionarLista): Prioritarias sigue siendo
            // candidatas.json; Secundarias y TramoBajo son nuevos, con la
            // misma lógica de "activas rescata lo que el diario no habría
            // detectado" aplicada a las tres por igual.
            var rutaPrioritarias = Path.Combine(repoRoot, "data", "candidatas.json");
            var rutaSecundarias = Path.Combine(repoRoot, "data", "secundarias.json");
            var rutaTramoBajo = Path.Combine(repoRoot, "data", "tramo_bajo.json");

            var (todasPrioritariasSinOverride, nuevasPrioritariasDiario, nuevasPrioritariasActivas) = FusionarLista(
                rutaPrioritarias, resultadoDiario.Prioritarias, resultadoActivas?.Prioritarias,
                fecha, fechaActivas, tramo: null, tiposPrivados);

            var (todasSecundariasSinOverride, nuevasSecundariasDiario, nuevasSecundariasActivas) = FusionarLista(
                rutaSecundarias, resultadoDiario.Secundarias, resultadoActivas?.Secundarias,
                fecha, fechaActivas, tramo: null, tiposPrivados);

            var (todasTramoBajo, nuevasTramoBajoDiario, nuevasTramoBajoActivas) = FusionarLista(
                rutaTramoBajo, resultadoDiario.TramoBajo, resultadoActivas?.TramoBajo,
                fecha, fechaActivas, tramo: "bajo", tiposPrivados);

            // Overrides humanos (ver AplicadorOverrides, data/overrides.json):
            // nunca expiran, se reaplican en cada corrida sobre las listas ya
            // fusionadas — ANTES de que candidatas.json/secundarias.json
            // toquen disco. TramoBajo nunca participa.
            var overrides = JsonStore.CargarOPredeterminado(
                Path.Combine(repoRoot, "data", "overrides.json"), JsonOpciones.Persistencia,
                new Dictionary<string, EntradaOverride>());
            var (todasPrioritarias, todasSecundarias, _) = AplicadorOverrides.Aplicar(
                overrides, todasPrioritariasSinOverride, todasSecundariasSinOverride);

            JsonStore.Guardar(rutaPrioritarias, todasPrioritarias, JsonOpciones.Persistencia);
            JsonStore.Guardar(rutaSecundarias, todasSecundarias, JsonOpciones.Persistencia);
            JsonStore.Guardar(rutaTramoBajo, todasTramoBajo, JsonOpciones.Persistencia);

            // El filtro de estado solo se aplica al capturar — de ahí en
            // adelante las listas nunca vuelven a consultar el estado y
            // acumularían licitaciones cerradas para siempre. El lote diario
            // crudo (antes de filtrar por estado) ya trae esas transiciones
            // gratis: si un código de una lista aparece hoy con estado
            // distinto de Publicada, se mueve a data/historico/ — salvo que
            // ya tenga triage humano encima, que se respeta tal cual.
            var (prioritariasActivas, prioritariasMovidas) = RevalidarEstado(
                rutaPrioritarias, Path.Combine(repoRoot, "data", "historico", "candidatas.json"),
                todasPrioritarias, loteDiario);

            var (secundariasActivas, secundariasMovidas) = RevalidarEstado(
                rutaSecundarias, Path.Combine(repoRoot, "data", "historico", "secundarias.json"),
                todasSecundarias, loteDiario);

            var (tramoBajoActivas, tramoBajoMovidas) = RevalidarEstado(
                rutaTramoBajo, Path.Combine(repoRoot, "data", "historico", "tramo_bajo.json"),
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
                    nuevasPrioritariasActivas.Count, nuevasSecundariasActivas.Count, nuevasTramoBajoActivas.Count,
                    enriquecidosActivasHoy, enriquecimientosFallidosActivasHoy,
                    resultadoActivas.Bienes, resultadoActivas.SinResolverUnspsc,
                    resultadoActivas.RevisionManualUnspsc, ahorradosPorAcumuladosActivas);

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
                barridoActivasFunnel,
                enriquecidosDiarioHoy,
                enriquecimientosFallidosDiarioHoy,
                resultadoDiario.Bienes,
                resultadoDiario.SinResolverUnspsc,
                resultadoDiario.RevisionManualUnspsc,
                ahorradosPorAcumuladosDiario);

            var informes = ActualizarSerieInformes(repoRoot, informeHoy);
            JsonStore.Guardar(Path.Combine(repoRoot, "data", "informes.json"), informes, JsonOpciones.Persistencia);

            var totalNuevasHoy = nuevasPrioritariasDiario.Count + nuevasPrioritariasActivas.Count
                + nuevasSecundariasDiario.Count + nuevasSecundariasActivas.Count
                + nuevasTramoBajoDiario.Count + nuevasTramoBajoActivas.Count;
            RegistrarEvento(repoRoot, informeHoy, totalNuevasHoy, totalMovidasHistorico);

            var dashboard = GeneradorDashboard.Construir(prioritariasActivas, secundariasActivas, tramoBajoActivas, informes, DateTime.UtcNow);
            JsonStore.Guardar(Path.Combine(repoRoot, "docs", "data.json"), dashboard, JsonOpciones.Persistencia);
            PublicarPanelRevisionConfig(repoRoot);

            ImprimirResumen(
                resultadoDiario, nuevasPrioritariasDiario.Count, nuevasSecundariasDiario.Count, nuevasTramoBajoDiario.Count,
                enriquecidosDiarioHoy, enriquecimientosFallidosDiarioHoy, ahorradosPorAcumuladosDiario,
                estadoActivas, resultadoActivas, enriquecidosActivasHoy, enriquecimientosFallidosActivasHoy,
                ahorradosPorAcumuladosActivas);

            return 0;
        }
        catch (MercadoPublicoApiException ex)
        {
            Console.Error.WriteLine($"[FATAL] Pipeline abortado sin escribir cambios: {ex.Message}");
            return 1;
        }
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
        HashSet<string> tiposPrivados)
    {
        var existentes = JsonStore.CargarOPredeterminado(rutaArchivo, JsonOpciones.Persistencia, new List<Candidata>());
        var codigosExistentes = existentes.Select(c => c.Codigo).ToHashSet();
        var codigosDiario = detectadasDiario.Select(c => c.Origen.CodigoExterno).ToHashSet();

        var nuevasDiario = detectadasDiario
            .Where(c => !codigosExistentes.Contains(c.Origen.CodigoExterno))
            .Select(c => CrearCandidata(c, fechaDiario, OrigenCandidata.Diario, tramo, tiposPrivados))
            .ToList();

        var nuevasActivas = detectadasActivas is null
            ? new List<Candidata>()
            : detectadasActivas
                .Where(c => !codigosExistentes.Contains(c.Origen.CodigoExterno) && !codigosDiario.Contains(c.Origen.CodigoExterno))
                .Select(c => CrearCandidata(c, fechaActivas, OrigenCandidata.Activas, tramo, tiposPrivados))
                .ToList();

        var todas = existentes.Concat(nuevasDiario).Concat(nuevasActivas).ToList();

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
                MigrarCamposUnspsc(informe);
                MigrarCamposRevisionManualYAcumulados(informe);
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
        int enriquecidosDiario, int enriquecimientosFallidosDiario, int ahorradosPorAcumuladosDiario,
        string estadoActivas, ResultadoFiltro? resultadoActivas, int enriquecidosActivas, int enriquecimientosFallidosActivas,
        int ahorradosPorAcumuladosActivas)
    {
        Console.WriteLine("== Resumen de la corrida ==");
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
                var estadoCierre = MapearEstadoDeCierre(licitacion.CodigoEstado);
                if (estadoCierre is null)
                {
                    Console.Error.WriteLine(
                        $"[ADVERTENCIA] Estado no documentado ({licitacion.CodigoEstado}) en {candidata.Codigo}, " +
                        "tratado como Cerrada por defecto.");
                }

                candidata.EstadoFlujo = estadoCierre ?? EstadoFlujo.Cerrada;
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

            if (candidata.EstadoFlujo != EstadoFlujo.Pendiente)
            {
                tramoBajoActivas.Add(candidata);
                continue;
            }

            if (licitacion.CodigoEstado != 5)
            {
                var estadoCierre = MapearEstadoDeCierre(licitacion.CodigoEstado);
                if (estadoCierre is null)
                {
                    Console.Error.WriteLine(
                        $"[ADVERTENCIA] Estado no documentado ({licitacion.CodigoEstado}) en {candidata.Codigo}, " +
                        "tratado como Cerrada por defecto.");
                }

                candidata.EstadoFlujo = estadoCierre ?? EstadoFlujo.Cerrada;
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
}
