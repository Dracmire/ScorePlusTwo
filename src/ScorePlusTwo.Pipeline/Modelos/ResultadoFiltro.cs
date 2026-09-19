namespace ScorePlusTwo.Pipeline.Modelos;

// Tipo intermedio que produce el filtro puro — no se persiste tal cual.
// RubroMatch/TerminoMatch son null para tramo bajo (L1 no pasa por
// clasificación de rubro) y para secundarias que no matchearon ningún rubro
// (inventario crudo de prospección). UnspscEstado/CodigosProductoUnspsc
// quedan en su default (PendienteEnriquecimiento/vacío) para TramoBajo y
// tipos privados — nunca se enriquecen, por la misma razón que nunca pasan
// por rubro (ver FiltroLicitaciones). Region viene de EntradaCacheUnspsc.
// RegionUnidad (F2, 2026-09-17) cuando hubo enriquecimiento — null si nunca
// se enriqueció.
// EsRevisionAmbigua (2026-09-19): true cuando el unico termino de rubro que
// matcheo esta en RubroCriterio.TerminosAmbiguos y por lo demas habria
// calificado para Prioritarias (rubro alta + region elegible) — Program.
// CrearCandidata lo traduce a EstadoFlujo.RevisionAmbigua al crear la
// candidata. Siempre false para TramoBajo (nunca se auto-promueve de todas
// formas, asi que la distincion no aplica).
public sealed record CandidataDetectada(
    LicitacionRaw Origen,
    string Tipo,
    string? RubroMatch,
    string? TerminoMatch,
    UnspscEstado UnspscEstado = UnspscEstado.PendienteEnriquecimiento,
    IReadOnlyList<int>? CodigosProductoUnspsc = null,
    string? Region = null,
    string? Moneda = null,
    decimal? Monto = null,
    int? CantidadReclamos = null,
    bool EsRevisionAmbigua = false);

// Una licitación que sobrevivió estado+tipo+descarte_duro, todavía sin
// clasificar por rubro. Tipo ya viene resuelto (derivado de CodigoExterno
// en la etapa 2) para no volver a parsearlo.
public sealed record CandidataParcial(LicitacionRaw Licitacion, string Tipo);

// Salida de FiltroLicitaciones.FiltrarHastaDescarteDuro (pura, sin cache ni
// catálogo): separa los tres destinos de tipo (tramo bajo / privado /
// regular) ANTES de decidir rubro, porque solo los "Regular" necesitan
// enriquecimiento UNSPSC — TramoBajo y TipoPrivado nunca pasan por rubro,
// así que tampoco tiene sentido gastar una llamada de detalle en ellos.
// El llamador (Program.cs) usa Regular para calcular qué CodigoExterno le
// faltan al cache antes de llamar a ClasificarYFiltrarRubro.
public sealed record SobrevivientesDescarteDuro(
    int Total,
    int TrasEstado,
    int TrasTipo,
    int DescarteDuro,
    IReadOnlyList<CandidataParcial> TramoBajo,
    IReadOnlyList<CandidataParcial> TipoPrivado,
    IReadOnlyList<CandidataParcial> Regular);

public sealed record ResultadoFiltro(
    int Total,
    int TrasEstado,
    int TrasTipo,
    // Descarte real: obras públicas y suministros (config.DescarteDuro).
    // Es el único conteo de algo que desaparece sin dejar rastro en ninguna
    // de las tres listas — todo lo demás se clasifica, no se destruye.
    int DescarteDuro,
    // Cuenta los UnspscEstado.Servicio con región elegible (F2,
    // 2026-09-17) — no condiciona si rubro corre (rubro se evalúa SIEMPRE
    // para Servicio, la región solo decide si el resultado llega a
    // Prioritarias o se queda en Secundarias, ver ClasificarYFiltrarRubro).
    // Superconjunto de Prioritarias.Count, nunca menor.
    int TrasRegion,
    // Lista A: matchean un rubro de prioridad "alta". Es lo que hoy va al
    // tablero.
    IReadOnlyList<CandidataDetectada> Prioritarias,
    // Lista B: sobreviven estado+tipo+descarte_duro pero no entran a
    // Prioritarias (rubro secundario, sin rubro, o tipo privado).
    // Inventario para prospectar rubros no atendidos.
    IReadOnlyList<CandidataDetectada> Secundarias,
    // Tipo L1: aceptado pero no se mezcla con el resto — no pasa por
    // clasificación de rubro, solo por descarte_duro. Opción solo si aparece
    // un cliente que la tome.
    IReadOnlyList<CandidataDetectada> TramoBajo,
    // Cuántos de los sobrevivientes "Regular" (ver SobrevivientesDescarteDuro)
    // resolvieron a UnspscEstado.Bien — nunca llegaron a evaluarse contra
    // rubro. Calculado en el mismo recorrido que clasifica, sin una segunda
    // pasada — ver ClasificarYFiltrarRubro.
    int Bienes,
    // SinResolver + PendienteEnriquecimiento juntos: lo que UNSPSC no pudo
    // confirmar como servicio, por catálogo o por falta de dato — visible
    // para informes.json, nunca mezclado en silencio con Bienes.
    int SinResolverUnspsc,
    // Cuántos de los sobrevivientes "Regular" resolvieron a
    // UnspscEstado.RevisionManual (F2, 2026-09-17) — familia UNSPSC donde
    // la clase de 8 dígitos no alcanza para decidir bien-vs-servicio (ver
    // UnspscEstado.RevisionManual). Nunca se mezcla con Bienes: es un
    // "no sabemos" de modalidad, no un "es un bien" con confianza.
    int RevisionManualUnspsc);
