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
    // UrlFicha queda en null en F1: el patrón "?idlicitacion={codigo}" no
    // pudo verificarse (el dominio mercadopublico.cl está bloqueado por el
    // proxy de red del entorno de desarrollo — no se pudo confirmar ni
    // descartar). Lo único documentado que sí funciona es la forma
    // "?qs=<cadena codificada>", que no es el código plano. Un link no
    // verificado que resulte roto cuesta más en una demo a clientes que no
    // tener link — así que el tablero muestra el código como texto plano
    // copiable (ver docs/app.js) hasta confirmar el patrón correcto contra
    // un caso real y completar este método con un solo cambio localizado.
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
                UrlFicha: null))
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
