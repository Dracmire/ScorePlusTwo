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
    string? Moneda,
    decimal? Monto,
    EstadoFlujo EstadoFlujo,
    string? UrlFicha);

public sealed record DashboardSerieItem(DateOnly Fecha, int Total, int Candidatas, double Tasa);

public sealed record DashboardData(
    DateTime GeneradoEn,
    IReadOnlyList<DashboardCandidata> Candidatas,
    IReadOnlyList<DashboardSerieItem> SerieTasaRubro);

public static class GeneradorDashboard
{
    // UrlFicha queda en null: el patrón "?idlicitacion={codigo}" NO resuelve
    // a la ficha del código pedido — es estado de sesión de ASP.NET, no un
    // parámetro independiente. Caso concreto que lo confirmó: se pidió
    // "?idlicitacion=598-16-LE26" y el servidor devolvió la ficha de
    // "976-28-O125" (la que se había pedido en una verificación anterior),
    // redirigiendo igual a la forma "?qs=<cadena codificada>". Una demo a
    // clientes mostraría la licitación equivocada, que es peor que no tener
    // link — así que el tablero muestra el código como texto plano copiable
    // (ver docs/app.js). No reintentar este patrón sin una forma de probarlo
    // contra dos códigos distintos en la misma sesión del navegador.
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
                Moneda: c.Moneda,
                Monto: c.Monto,
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
