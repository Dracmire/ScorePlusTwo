namespace ScorePlusTwo.Pipeline.Modelos;

// Mini-funnel reutilizado tanto para los conteos principales del día (lote
// diario) como para el barrido `activas` cuando corre — misma forma, series
// separadas para no contaminar la calibración del lote diario (ver
// BarridoActivas en InformeDiario).
//
// NuevasPrioritarias/NuevasSecundarias/NuevasTramoBajo son hechos históricos
// — cuántas aparecieron por primera vez ese día — no derivables del estado
// actual de candidatas.json/secundarias.json/tramo_bajo.json. Se preservan
// al reprocesar una fecha (ver ActualizarSerieInformes en Program.cs); el
// resto de los conteos sí describe el estado actual del lote y se actualiza
// en cada reproceso.
public sealed record InformeFunnel(
    int Total,
    int TrasEstado,
    int TrasTipo,
    int TrasRegion,
    int DescarteDuro,
    int Prioritarias,
    int Secundarias,
    int TramoBajo,
    int NuevasPrioritarias,
    int NuevasSecundarias,
    int NuevasTramoBajo);

public sealed record InformeDiario(
    DateOnly Fecha,
    int Total,
    int TrasEstado,
    int TrasTipo,
    int TrasRegion,
    int DescarteDuro,
    int Prioritarias,
    int Secundarias,
    int TramoBajo,
    int NuevasPrioritarias,
    int NuevasSecundarias,
    int NuevasTramoBajo,
    InformeFunnel? BarridoActivas);
