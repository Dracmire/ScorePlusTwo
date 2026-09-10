using ScorePlusTwo.Pipeline.Modelos;

namespace ScorePlusTwo.Pipeline.Dashboard;

public sealed record DashboardCandidata(
    string Codigo,
    string Nombre,
    string Tipo,
    DateTime? FechaCierre,
    string? RubroMatch,
    string? TerminoMatch,
    string? Region,
    string? Organismo,
    string? Moneda,
    decimal? Monto,
    EstadoFlujo EstadoFlujo,
    OrigenCandidata Origen,
    DateOnly FechaLote,
    string? Tramo,
    bool TipoPrivado,
    string? UrlFicha);

public sealed record DashboardSerieItem(DateOnly Fecha, int Total, int Prioritarias, double Tasa);

public sealed record DashboardData(
    DateTime GeneradoEn,
    IReadOnlyList<DashboardCandidata> Prioritarias,
    IReadOnlyList<DashboardCandidata> Secundarias,
    IReadOnlyList<DashboardCandidata> TramoBajo,
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
    public static DashboardData Construir(
        IEnumerable<Candidata> prioritarias,
        IEnumerable<Candidata> secundarias,
        IEnumerable<Candidata> tramoBajo,
        IEnumerable<InformeDiario> informes,
        DateTime ahora)
    {
        static IReadOnlyList<DashboardCandidata> Mapear(IEnumerable<Candidata> candidatas) => candidatas
            .OrderBy(c => c.FechaCierre ?? DateTime.MaxValue)
            .Select(c => new DashboardCandidata(
                Codigo: c.Codigo,
                Nombre: c.Nombre,
                Tipo: c.Tipo,
                FechaCierre: c.FechaCierre,
                RubroMatch: c.RubroMatch,
                TerminoMatch: c.TerminoMatch,
                Region: c.Region,
                Organismo: c.Organismo,
                Moneda: c.Moneda,
                Monto: c.Monto,
                EstadoFlujo: c.EstadoFlujo,
                Origen: c.Origen,
                FechaLote: c.FechaLote,
                Tramo: c.Tramo,
                TipoPrivado: c.TipoPrivado,
                UrlFicha: null))
            .ToList();

        var serie = informes
            .OrderBy(i => i.Fecha)
            .Select(i => new DashboardSerieItem(
                Fecha: i.Fecha,
                Total: i.Total,
                Prioritarias: i.Prioritarias,
                Tasa: i.Total == 0 ? 0 : Math.Round((double)i.Prioritarias / i.Total, 4)))
            .ToList();

        return new DashboardData(ahora, Mapear(prioritarias), Mapear(secundarias), Mapear(tramoBajo), serie);
    }
}
