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
    int NuevasTramoBajo,
    // F2 (2026-09-16): visibilidad de la etapa de enriquecimiento UNSPSC.
    // EnriquecidosHoy/EnriquecimientosFallidos son hechos de ESTA corrida
    // (cuántos cache-miss se resolvieron/fallaron al llamar a la API) — a
    // diferencia de NuevasPrioritarias, no se preservan al reprocesar una
    // fecha: si se reprocesa, ya no hay cache-miss que enriquecer (o los que
    // quedan son otros), así que 0 en un reproceso es el valor correcto, no
    // un bug. Bienes/SinResolverUnspsc SÍ describen el estado actual del
    // lote (igual que Prioritarias/Secundarias) y se recalculan siempre.
    int EnriquecidosHoy = 0,
    int EnriquecimientosFallidos = 0,
    int Bienes = 0,
    int SinResolverUnspsc = 0);

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
    InformeFunnel? BarridoActivas,
    int EnriquecidosHoy = 0,
    int EnriquecimientosFallidos = 0,
    int Bienes = 0,
    int SinResolverUnspsc = 0);
