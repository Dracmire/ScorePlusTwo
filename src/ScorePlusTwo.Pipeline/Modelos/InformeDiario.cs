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
    int SinResolverUnspsc = 0,
    // F2 (2026-09-17): RevisionManualUnspsc describe el estado actual del
    // lote (se recalcula siempre, igual que Bienes/SinResolverUnspsc).
    // AhorradosPorAcumulados es un hecho de ESTA corrida (cuántos
    // CodigoExterno ya confirmados como Prioritarias se excluyeron de
    // enriquecimiento/reclasificación, ver FiltrarConEnriquecimientoAsync)
    // — mismo criterio que EnriquecidosHoy, no se preserva al reprocesar.
    // Es el "presupuesto disponible" para poder seguir achicando
    // descarte_duro sin quedar contra el límite de tiempo de Actions.
    int RevisionManualUnspsc = 0,
    int AhorradosPorAcumulados = 0);

// ReverificadasHoy/ReverificacionesConCambioFecha/ReverificacionesATerminal/
// ReverificacionesFallidas/CierresDetectadosEnTriage (2026-09-30, ver
// Verificacion/ReverificacionService.cs): visibilidad del paso automático
// de re-verificación contra el detalle real de la API — permite notar si
// deja de funcionar sin que nadie lo note. Suman Prioritarias +
// Secundarias/TramoBajo combinados (mismo criterio que EnriquecidosHoy:
// hechos de ESTA corrida, no se preservan al reprocesar una fecha).
//
// LlamadasEnriquecimiento/LlamadasReverificacion/LlamadasBarridoActivas
// (2026-09-30): desglose de cuota de llamadas a la API de detalle por
// categoría — enriquecimiento (UNSPSC, diario+activas combinados),
// re-verificación (este follow-up, Prioritarias+Secundarias/TramoBajo
// combinados) y barrido activas (la llamada de LISTADO ObtenerActivasAsync,
// 1 cuando corre, 0 si no) — para notar si el consumo de cuota crece de
// forma inesperada sin sumar a mano varios contadores ya existentes.
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
    int SinResolverUnspsc = 0,
    int RevisionManualUnspsc = 0,
    int AhorradosPorAcumulados = 0,
    int ReverificadasHoy = 0,
    int ReverificacionesConCambioFecha = 0,
    int ReverificacionesATerminal = 0,
    int ReverificacionesFallidas = 0,
    int CierresDetectadosEnTriage = 0,
    int LlamadasEnriquecimiento = 0,
    int LlamadasReverificacion = 0,
    int LlamadasBarridoActivas = 0);
