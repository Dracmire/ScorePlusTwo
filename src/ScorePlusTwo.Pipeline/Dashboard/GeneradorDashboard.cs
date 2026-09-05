using ScorePlusTwo.Pipeline.Modelos;

namespace ScorePlusTwo.Pipeline.Dashboard;

public sealed record DashboardCandidata(
    string Codigo,
    string Nombre,
    string Tipo,
    DateTime? FechaCierre,
    int? DiasParaCierre,
    string RubroMatch,
    string TerminoMatch,
    string? Region,
    string? Organismo,
    EstadoFlujo EstadoFlujo,
    string? UrlFicha);

public sealed record DashboardSerieItem(DateOnly Fecha, int Total, int Candidatas, double Tasa);

public sealed record DashboardData(
    DateTime GeneradoEn,
    IReadOnlyList<DashboardCandidata> Candidatas,
    IReadOnlyList<DashboardSerieItem> SerieTasaRubro);

public static class GeneradorDashboard
{
    // Patrón verificado externamente (fuera de este entorno, cuyo proxy de
    // red bloquea mercadopublico.cl) contra la ficha real de la licitación
    // 976-28-O125: el servidor acepta el código plano como "idlicitacion" y
    // redirige internamente a la forma "?qs=<cadena codificada>". Resuelve
    // correctamente a la ficha — no volver a cuestionar este patrón sin
    // evidencia de que dejó de funcionar.
    private const string PatronUrlFicha =
        "https://www.mercadopublico.cl/Procurement/Modules/RFB/DetailsAcquisition.aspx?idlicitacion={0}";

    public static DashboardData Construir(IEnumerable<Candidata> candidatas, IEnumerable<InformeDiario> informes, DateTime ahora)
    {
        var candidatasOrdenadas = candidatas
            .OrderBy(c => c.FechaCierre ?? DateTime.MaxValue)
            .Select(c => new DashboardCandidata(
                Codigo: c.Codigo,
                Nombre: c.Nombre,
                Tipo: c.Tipo,
                FechaCierre: c.FechaCierre,
                DiasParaCierre: c.FechaCierre is { } fechaCierre
                    ? (int)Math.Ceiling((fechaCierre - ahora).TotalDays)
                    : null,
                RubroMatch: c.RubroMatch,
                TerminoMatch: c.TerminoMatch,
                Region: c.Region,
                Organismo: c.Organismo,
                EstadoFlujo: c.EstadoFlujo,
                UrlFicha: string.Format(PatronUrlFicha, Uri.EscapeDataString(c.Codigo))))
            .ToList();

        var serie = informes
            .OrderBy(i => i.Fecha)
            .Select(i => new DashboardSerieItem(
                Fecha: i.Fecha,
                Total: i.Total,
                Candidatas: i.Candidatas,
                Tasa: i.Total == 0 ? 0 : Math.Round((double)i.Candidatas / i.Total, 4)))
            .ToList();

        return new DashboardData(ahora, candidatasOrdenadas, serie);
    }
}
