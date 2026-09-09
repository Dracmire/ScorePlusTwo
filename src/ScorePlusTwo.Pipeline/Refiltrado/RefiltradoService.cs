using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScorePlusTwo.Pipeline.Filtro;
using ScorePlusTwo.Pipeline.Modelos;
using ScorePlusTwo.Pipeline.Persistencia;

namespace ScorePlusTwo.Pipeline.Refiltrado;

// Una fila del CSV de salida. archivo_origen/fecha_lote son lo que permite
// distinguir de qué lote (diario o activas) salió cada candidata al
// re-filtrar todo el histórico acumulado en data/raw/.
public sealed record FilaRefiltrado(
    string Codigo,
    string Nombre,
    string Tipo,
    string? RubroMatch,
    string? TerminoMatch,
    DateTime? FechaCierre,
    string ArchivoOrigen,
    DateOnly FechaLote);

public sealed record ResultadoRefiltrado(
    int ArchivosProcesados,
    int RegistrosTotales,
    int CandidatasConDuplicados,
    IReadOnlyList<FilaRefiltrado> Candidatas);

// Re-filtra todo el crudo acumulado en data/raw/ con un criterios.json
// alternativo — para mostrarle a un cliente de un rubro nuevo qué habría
// capturado el sistema sobre lo ya acumulado, o para probar un cambio de
// criterios contra el histórico antes de aplicarlo. Es de solo lectura sobre
// el estado de producción: nunca escribe candidatas.json/informes.json/
// eventos.json/docs/data.json — ver Program.EjecutarRefiltrado, que lo aísla
// del flujo automático en una rama de Main completamente aparte.
public static class RefiltradoService
{
    private static readonly Regex PatronArchivo = new(
        @"^(?:activas-)?(?<fecha>\d{4}-\d{2}-\d{2})\.json$", RegexOptions.Compiled);

    // Extrae la fecha de un nombre de archivo de data/raw/ (ej.
    // "2026-09-04.json" o "activas-2026-09-05.json"). Devuelve null para lo
    // que no matchea (ej. ".gitkeep") en vez de lanzar, para que Ejecutar
    // pueda ignorar esos archivos sin crashear el barrido completo — mismo
    // principio que CodigoExternoParser.TryExtraerTipoAnio.
    public static DateOnly? ExtraerFechaDeArchivo(string nombreArchivo)
    {
        var match = PatronArchivo.Match(nombreArchivo);
        if (!match.Success)
        {
            return null;
        }

        return DateOnly.ParseExact(match.Groups["fecha"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static ResultadoRefiltrado Ejecutar(string directorioRaw, Criterios criterios, DateOnly? desde)
    {
        var archivos = Directory.EnumerateFiles(directorioRaw, "*.json")
            .Select(ruta => (Ruta: ruta, Fecha: ExtraerFechaDeArchivo(Path.GetFileName(ruta))))
            .Where(a => a.Fecha is not null && (desde is null || a.Fecha >= desde))
            .OrderBy(a => a.Fecha)
            .ToList();

        // Dictionary indexado por CodigoExterno: como los archivos se
        // procesan en orden de fecha ascendente, sobrescribir la entrada en
        // cada match implementa "se queda con la aparición más reciente"
        // sin necesitar una etapa de dedupe aparte.
        var candidatasPorCodigo = new Dictionary<string, FilaRefiltrado>();
        var registrosTotales = 0;
        var candidatasConDuplicados = 0;

        foreach (var (ruta, fecha) in archivos)
        {
            var respuesta = JsonStore.Cargar<ListadoLicitacionesResponse>(ruta, JsonOpciones.ApiLectura);
            registrosTotales += respuesta.Listado.Count;

            var resultado = FiltroLicitaciones.Filtrar(respuesta.Listado, criterios);
            candidatasConDuplicados += resultado.Prioritarias.Count;

            var nombreArchivo = Path.GetFileName(ruta);
            foreach (var candidata in resultado.Prioritarias)
            {
                candidatasPorCodigo[candidata.Origen.CodigoExterno] = new FilaRefiltrado(
                    candidata.Origen.CodigoExterno,
                    candidata.Origen.Nombre,
                    candidata.Tipo,
                    candidata.RubroMatch,
                    candidata.TerminoMatch,
                    candidata.Origen.FechaCierre,
                    nombreArchivo,
                    fecha!.Value);
            }
        }

        var filas = candidatasPorCodigo.Values
            .OrderBy(f => f.FechaCierre ?? DateTime.MaxValue)
            .ToList();

        return new ResultadoRefiltrado(archivos.Count, registrosTotales, candidatasConDuplicados, filas);
    }

    public static string GenerarCsv(IReadOnlyList<FilaRefiltrado> filas)
    {
        var sb = new StringBuilder();
        sb.Append("codigo,nombre,tipo,rubro_match,termino_match,fecha_cierre,archivo_origen,fecha_lote\r\n");

        foreach (var f in filas)
        {
            sb.Append(string.Join(",",
                Escapar(f.Codigo),
                Escapar(f.Nombre),
                Escapar(f.Tipo),
                Escapar(f.RubroMatch ?? string.Empty),
                Escapar(f.TerminoMatch ?? string.Empty),
                Escapar(f.FechaCierre?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty),
                Escapar(f.ArchivoOrigen),
                Escapar(f.FechaLote.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    // RFC4180, mismo criterio que docs/app.js (csvEscapar). El problema real
    // que motiva tenerlo bien es el opuesto: el CSV que exporta ChileCompra
    // usa ';' sin encomillar campos con ';' embebido y desalinea columnas en
    // silencio (ver JsonStore.cs, caso 85-34-LP26).
    private static string Escapar(string valor)
    {
        if (valor.IndexOfAny(new[] { '"', ',', '\n', '\r' }) >= 0)
        {
            return "\"" + valor.Replace("\"", "\"\"") + "\"";
        }

        return valor;
    }
}
