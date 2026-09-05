namespace ScorePlusTwo.Pipeline.Modelos;

// Tipo intermedio que produce el filtro puro — no se persiste tal cual.
public sealed record CandidataDetectada(
    LicitacionRaw Origen, string Tipo, string RubroMatch, string TerminoMatch);

// Un registro que matcheó un rubro con `activo: false` — señal de mercado,
// no candidata operable (ver rubro "ia" en config/criterios.json).
public sealed record Observacion(
    string Codigo, string Nombre, string Rubro, string TerminoMatch);

public sealed record ResultadoFiltro(
    int Total,
    int TrasEstado,
    int TrasTipo,
    int Excluidas,
    int TrasRubro,
    int TrasRegion,
    IReadOnlyList<CandidataDetectada> Candidatas,
    IReadOnlyList<Observacion> Observaciones);
