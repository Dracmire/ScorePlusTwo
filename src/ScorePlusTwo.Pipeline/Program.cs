using ScorePlusTwo.Pipeline.Api;
using ScorePlusTwo.Pipeline.Cli;
using ScorePlusTwo.Pipeline.Dashboard;
using ScorePlusTwo.Pipeline.Filtro;
using ScorePlusTwo.Pipeline.Infraestructura;
using ScorePlusTwo.Pipeline.Modelos;
using ScorePlusTwo.Pipeline.Persistencia;

namespace ScorePlusTwo.Pipeline;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var opciones = OpcionesCli.Parse(args);
            var repoRoot = RutaRepo.Resolver();

            List<LicitacionRaw> loteDiario;
            DateOnly fecha;
            ResultadoFiltro? resultadoActivas = null;

            if (opciones.RutaFixture is not null)
            {
                // Modo local: sin red, sin MP_TICKET, sin barrido activas.
                fecha = opciones.Fecha ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
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
                fecha = opciones.Fecha ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

                using var http = new HttpClient();
                var cliente = new MercadoPublicoClient(http, ticket);

                // Diario + adjudicada: fatal si cualquiera falla, nada se persiste ese día.
                var respuestaDiaria = await cliente.ObtenerListadoDiarioAsync(fecha);
                var respuestaAdjudicada = await cliente.ObtenerAdjudicadasAsync(fecha);

                GuardarRawDelDia(repoRoot, fecha, respuestaDiaria);
                AcumularAdjudicadas(repoRoot, fecha, respuestaAdjudicada.Listado);
                loteDiario = respuestaDiaria.Listado;

                if (CorrespondeBarridoActivas(repoRoot))
                {
                    // Asimetría deliberada: un fallo aquí NUNCA es fatal para el resto del pipeline.
                    try
                    {
                        var respuestaActivas = await cliente.ObtenerActivasAsync();
                        GuardarRawActivas(repoRoot, DateOnly.FromDateTime(DateTime.UtcNow), respuestaActivas);

                        var criteriosParaActivas = JsonStore.Cargar<Criterios>(
                            Path.Combine(repoRoot, "config", "criterios.json"), JsonOpciones.Config);
                        resultadoActivas = FiltroLicitaciones.Filtrar(respuestaActivas.Listado, criteriosParaActivas);
                    }
                    catch (MercadoPublicoApiException ex)
                    {
                        Console.Error.WriteLine($"[ADVERTENCIA] Barrido 'activas' omitido este día: {ex.Message}");
                    }
                }
            }

            var criterios = JsonStore.Cargar<Criterios>(
                Path.Combine(repoRoot, "config", "criterios.json"), JsonOpciones.Config);

            var resultadoDiario = FiltroLicitaciones.Filtrar(loteDiario, criterios);

            var existentes = JsonStore.CargarOPredeterminado(
                Path.Combine(repoRoot, "data", "candidatas.json"), JsonOpciones.Persistencia, new List<Candidata>());
            var codigosExistentes = existentes.Select(c => c.Codigo).ToHashSet();
            var codigosDiario = resultadoDiario.Candidatas.Select(c => c.Origen.CodigoExterno).ToHashSet();

            var nuevasDiario = resultadoDiario.Candidatas
                .Where(c => !codigosExistentes.Contains(c.Origen.CodigoExterno))
                .Select(c => CrearCandidata(c, fecha, OrigenCandidata.Diario))
                .ToList();

            var nuevasActivas = resultadoActivas is null
                ? new List<Candidata>()
                : resultadoActivas.Candidatas
                    .Where(c => !codigosExistentes.Contains(c.Origen.CodigoExterno) && !codigosDiario.Contains(c.Origen.CodigoExterno))
                    .Select(c => CrearCandidata(c, DateOnly.FromDateTime(DateTime.UtcNow), OrigenCandidata.Activas))
                    .ToList();

            var todasLasCandidatas = existentes.Concat(nuevasDiario).Concat(nuevasActivas).ToList();
            JsonStore.Guardar(Path.Combine(repoRoot, "data", "candidatas.json"), todasLasCandidatas, JsonOpciones.Persistencia);

            var barridoActivasFunnel = resultadoActivas is null
                ? null
                : new InformeFunnel(
                    resultadoActivas.Total, resultadoActivas.TrasEstado, resultadoActivas.TrasTipo,
                    resultadoActivas.TrasRegion, resultadoActivas.Excluidas, resultadoActivas.Candidatas.Count,
                    nuevasActivas.Count);

            var informeHoy = new InformeDiario(
                fecha,
                resultadoDiario.Total,
                resultadoDiario.TrasEstado,
                resultadoDiario.TrasTipo,
                resultadoDiario.TrasRegion,
                resultadoDiario.Excluidas,
                resultadoDiario.Candidatas.Count,
                nuevasDiario.Count,
                resultadoDiario.Observaciones,
                barridoActivasFunnel);

            var informes = ActualizarSerieInformes(repoRoot, informeHoy);
            JsonStore.Guardar(Path.Combine(repoRoot, "data", "informes.json"), informes, JsonOpciones.Persistencia);

            RegistrarEvento(repoRoot, informeHoy, nuevasDiario.Count + nuevasActivas.Count);

            var dashboard = GeneradorDashboard.Construir(todasLasCandidatas, informes, DateTime.UtcNow);
            JsonStore.Guardar(Path.Combine(repoRoot, "docs", "data.json"), dashboard, JsonOpciones.Persistencia);

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
    // huso horario de Chile (no UTC: el cron corre a las 09:00 UTC, que
    // puede caer en domingo o lunes en Chile según la época del año).
    private static bool CorrespondeBarridoActivas(string repoRoot)
    {
        var directorioRaw = Path.Combine(repoRoot, "data", "raw");
        var yaHuboActivas = Directory.Exists(directorioRaw)
            && Directory.EnumerateFiles(directorioRaw, "activas-*.json").Any();

        if (!yaHuboActivas)
        {
            return true;
        }

        var zonaChile = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
        var ahoraChile = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zonaChile);
        return ahoraChile.DayOfWeek == DayOfWeek.Monday;
    }

    private static Candidata CrearCandidata(CandidataDetectada detectada, DateOnly fechaLote, OrigenCandidata origen) =>
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
        };

    // Idempotente por fecha: si ya existía una entrada para hoy (re-corrida
    // manual vía workflow_dispatch), la reemplaza en vez de duplicarla.
    private static List<InformeDiario> ActualizarSerieInformes(string repoRoot, InformeDiario informeHoy)
    {
        var ruta = Path.Combine(repoRoot, "data", "informes.json");
        var informes = JsonStore.CargarOPredeterminado(ruta, JsonOpciones.Persistencia, new List<InformeDiario>());
        informes.RemoveAll(i => i.Fecha == informeHoy.Fecha);
        informes.Add(informeHoy);
        return informes.OrderBy(i => i.Fecha).ToList();
    }

    private static void RegistrarEvento(string repoRoot, InformeDiario informe, int totalNuevas)
    {
        var ruta = Path.Combine(repoRoot, "data", "eventos.json");
        var eventos = JsonStore.CargarOPredeterminado(ruta, JsonOpciones.Persistencia, new List<EventoAuditoria>());

        var detalle = $"total={informe.Total} candidatas={informe.Candidatas} nuevas={totalNuevas}"
            + (informe.BarridoActivas is { } b ? $" activas_total={b.Total} activas_candidatas={b.Candidatas}" : string.Empty);

        eventos.Add(new EventoAuditoria(DateTime.UtcNow, "sistema", "corrida_pipeline", null, detalle));
        JsonStore.Guardar(ruta, eventos, JsonOpciones.Persistencia);
    }

    private static string FormatearFecha(DateOnly fecha) =>
        fecha.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
