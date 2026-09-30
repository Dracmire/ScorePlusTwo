namespace ScorePlusTwo.Pipeline.Modelos;

// Mini-funnel reutilizado tanto para los conteos principales del día (lote
// diario) como para el barrido `activas` cuando corre — misma forma, series
// separadas para no contaminar la calibración del lote diario (ver
// BarridoActivas en InformeDiario).
//
// Todos los campos de este record son "descubrimiento del lote" en el
// sentido de InformeDiario.Fusionar (ver más abajo): describen el lote
// crudo de una fecha tal como se vio la primera vez que se procesó, y se
// congelan ante reprocesos posteriores del mismo día. InformeFunnel en sí
// nunca tiene campos de "seguimiento/cuota" (esos solo existen a nivel de
// InformeDiario, ver abajo) — por eso el propio objeto BarridoActivas se
// trata como un solo campo de descubrimiento (se completa una vez si venía
// null, nunca se reemplaza después, ver Fusionar).
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
    int EnriquecidosHoy = 0,
    int EnriquecimientosFallidos = 0,
    int Bienes = 0,
    int SinResolverUnspsc = 0,
    int RevisionManualUnspsc = 0,
    int AhorradosPorAcumulados = 0);

// ReverificadasHoy/ReverificacionesConCambioFecha/ReverificacionesATerminal/
// ReverificacionesFallidas/CierresDetectadosEnTriage (2026-09-30, ver
// Verificacion/ReverificacionService.cs): visibilidad del paso automático
// de re-verificación contra el detalle real de la API — permite notar si
// deja de funcionar sin que nadie lo note. Suman Prioritarias +
// Secundarias/TramoBajo combinados.
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
    int LlamadasBarridoActivas = 0)
{
    // Campos de "descubrimiento del lote": describen el lote crudo de esa
    // Fecha tal como se vio la PRIMERA vez que se procesó exitosamente ese
    // día. Se fijan en esa primera corrida y quedan CONGELADOS ante
    // reprocesos posteriores del mismo día (ver Fusionar) — recalcularlos
    // en una segunda pasada los contamina con el filtro de acumulados
    // (FiltrarConEnriquecimientoAsync, Program.cs), que excluye de raíz
    // cualquier código que una corrida anterior ya haya confirmado como
    // Prioritaria: cuantas más veces se reprocesa el mismo día, menos
    // "nuevo" ve la clasificación, así que el embudo se degrada con cada
    // repetición en vez de describir el lote real. Confirmado con datos
    // reales el 2026-09-30: un workflow_dispatch manual sin flags,
    // disparado el mismo día que ya había corrido el cron, hizo caer
    // Prioritarias de 2 a 0 y EnriquecidosHoy de 324 a 0 para la misma
    // Fecha — ninguno de los dos números nuevos describía el lote del
    // 29-09, eran artefactos de la segunda pasada (ver el commit que
    // restauró data/informes.json ese día).
    //
    // BarridoActivas es la única excepción parcial: si quedó null en la
    // primera corrida (ej. `activas` falló ese día), SÍ se completa con
    // el de una corrida posterior donde sí corrió — pero una vez no-null,
    // también queda congelado.
    //
    // LIMITACIÓN CONOCIDA de esta regla: si se cambia config/criterios.json
    // (u otro insumo que afecte la clasificación) y se quiere que una
    // fecha YA PROCESADA hoy recalcule su embudo con el criterio nuevo,
    // Fusionar ya no lo permite — la entrada existente sigue ganando. Para
    // forzar un recálculo real de una fecha, hay que borrar a mano su
    // entrada de data/informes.json antes de disparar la corrida (con eso
    // Fusionar la trata como `existente is null` y usa el resultado nuevo
    // completo, sin fusionar nada).
    public static readonly HashSet<string> CamposDescubrimiento = new()
    {
        nameof(Total), nameof(TrasEstado), nameof(TrasTipo), nameof(TrasRegion), nameof(DescarteDuro),
        nameof(Prioritarias), nameof(Secundarias), nameof(TramoBajo),
        nameof(NuevasPrioritarias), nameof(NuevasSecundarias), nameof(NuevasTramoBajo),
        nameof(BarridoActivas),
        nameof(EnriquecidosHoy), nameof(EnriquecimientosFallidos), nameof(Bienes), nameof(SinResolverUnspsc),
        nameof(RevisionManualUnspsc), nameof(AhorradosPorAcumulados),
    };

    // Campos de "seguimiento y cuota": hechos de la corrida puntual que los
    // generó, no del lote original — a diferencia del descubrimiento, NO
    // se contaminan al repetirse (una re-verificación real siempre re-llama
    // a la API, sin importar cuántas veces se haya corrido antes ese día).
    // Por eso, en vez de congelarse o sobrescribirse, se SUMAN entre
    // corridas de la misma Fecha: así informes.json refleja el consumo/
    // actividad TOTAL del día aunque el pipeline haya corrido más de una
    // vez (cron + un workflow_dispatch manual, por ejemplo).
    public static readonly HashSet<string> CamposSeguimiento = new()
    {
        nameof(ReverificadasHoy), nameof(ReverificacionesConCambioFecha), nameof(ReverificacionesATerminal),
        nameof(ReverificacionesFallidas), nameof(CierresDetectadosEnTriage),
        nameof(LlamadasEnriquecimiento), nameof(LlamadasReverificacion), nameof(LlamadasBarridoActivas),
    };

    // Fusiona el resultado de una nueva corrida con la entrada ya existente
    // para la misma Fecha (si la hay) — reemplaza el `with { ... }` inline
    // que antes vivía en Program.ActualizarSerieInformes. Pura, testeable
    // sin red (ver ReverificacionServiceTests-style: InformeDiarioTests).
    //
    // NOTA para quien agregue un campo nuevo a este record: CamposTests
    // (InformeDiarioTests) verifica por reflexión que TODA propiedad de
    // InformeDiario (salvo Fecha) esté en exactamente una de
    // CamposDescubrimiento/CamposSeguimiento — un campo nuevo sin
    // clasificar rompe ese test en vez de quedar congelado en silencio por
    // el `existente with { }` de abajo.
    public static InformeDiario Fusionar(InformeDiario? existente, InformeDiario nuevo)
    {
        if (existente is null)
        {
            return nuevo;
        }

        return existente with
        {
            BarridoActivas = existente.BarridoActivas ?? nuevo.BarridoActivas,

            ReverificadasHoy = existente.ReverificadasHoy + nuevo.ReverificadasHoy,
            ReverificacionesConCambioFecha = existente.ReverificacionesConCambioFecha + nuevo.ReverificacionesConCambioFecha,
            ReverificacionesATerminal = existente.ReverificacionesATerminal + nuevo.ReverificacionesATerminal,
            ReverificacionesFallidas = existente.ReverificacionesFallidas + nuevo.ReverificacionesFallidas,
            CierresDetectadosEnTriage = existente.CierresDetectadosEnTriage + nuevo.CierresDetectadosEnTriage,
            LlamadasEnriquecimiento = existente.LlamadasEnriquecimiento + nuevo.LlamadasEnriquecimiento,
            LlamadasReverificacion = existente.LlamadasReverificacion + nuevo.LlamadasReverificacion,
            LlamadasBarridoActivas = existente.LlamadasBarridoActivas + nuevo.LlamadasBarridoActivas,
        };
    }
}
