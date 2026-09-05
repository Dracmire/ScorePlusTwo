namespace ScorePlusTwo.Pipeline.Modelos;

// Mini-funnel reutilizado tanto para los conteos principales del día (lote
// diario) como para el barrido `activas` cuando corre — misma forma, series
// separadas para no contaminar la calibración del lote diario (ver
// BarridoActivas en InformeDiario).
public sealed record InformeFunnel(
    int Total,
    int TrasEstado,
    int TrasTipo,
    int TrasRegion,
    int Excluidas,
    int Candidatas,
    int Nuevas);

public sealed record InformeDiario(
    DateOnly Fecha,
    int Total,
    int TrasEstado,
    int TrasTipo,
    int TrasRegion,
    int Excluidas,
    int Candidatas,
    int Nuevas,
    IReadOnlyList<Observacion> Observaciones,
    InformeFunnel? BarridoActivas);
