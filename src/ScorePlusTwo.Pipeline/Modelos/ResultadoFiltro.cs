namespace ScorePlusTwo.Pipeline.Modelos;

// Tipo intermedio que produce el filtro puro — no se persiste tal cual.
// RubroMatch/TerminoMatch son null para tramo bajo (L1 no pasa por
// clasificación de rubro) y para secundarias que no matchearon ningún rubro
// (inventario crudo de prospección).
public sealed record CandidataDetectada(
    LicitacionRaw Origen, string Tipo, string? RubroMatch, string? TerminoMatch);

public sealed record ResultadoFiltro(
    int Total,
    int TrasEstado,
    int TrasTipo,
    // Descarte real: obras públicas y suministros (config.DescarteDuro).
    // Es el único conteo de algo que desaparece sin dejar rastro en ninguna
    // de las tres listas — todo lo demás se clasifica, no se destruye.
    int DescarteDuro,
    // No-op en F1 (etapa identidad, ver comentario en FiltroLicitaciones),
    // aplicado solo sobre Prioritarias — mismo hook que dejaba el diseño
    // original para que F2 la reemplace sin reestructurar el resto.
    int TrasRegion,
    // Lista A: matchean un rubro de prioridad "alta" y no están bloqueadas
    // por exclusiones_rubro (bien vs. servicio). Es lo que hoy va al tablero.
    IReadOnlyList<CandidataDetectada> Prioritarias,
    // Lista B: sobreviven estado+tipo+descarte_duro pero no entran a
    // Prioritarias (rubro secundario, sin rubro, o bloqueadas por
    // exclusiones_rubro). Inventario para prospectar rubros no atendidos.
    IReadOnlyList<CandidataDetectada> Secundarias,
    // Tipo L1: aceptado pero no se mezcla con el resto — no pasa por
    // clasificación de rubro, solo por descarte_duro. Opción solo si aparece
    // un cliente que la tome.
    IReadOnlyList<CandidataDetectada> TramoBajo);
