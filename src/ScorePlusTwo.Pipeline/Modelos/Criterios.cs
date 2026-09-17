using System.Text.Json.Serialization;

namespace ScorePlusTwo.Pipeline.Modelos;

// Prioridad reemplaza al viejo booleano Activo: "alta" es lo que hoy va al
// tablero (Lista A), "secundaria" es inventario de prospección (Lista B) —
// ya no existe un tercer estado "inactivo" que borre matches sin dejar
// rastro (ver FiltroLicitaciones).
public sealed record RubroCriterio(string Id, string Prioridad, List<string> Terminos);

public sealed record Criterios(
    string Version,
    List<string> Tipos,
    List<int> Estados,
    List<string> Regiones,
    List<RubroCriterio> Rubros,
    // Descarte real: obras públicas y suministros, donde no hay negocio
    // posible. Es el único veto que hace desaparecer un registro por
    // completo — se evalúa antes que cualquier rubro, sobre tipos normales
    // y L1 por igual. Núcleo mínimo desde 2026-09-16 (F2): antes tenía
    // 40 términos y decidía bien-vs-servicio con varios ambiguos
    // ("equipos", "software"); ahora solo ahorra llamadas de enriquecimiento
    // sobre lo evidentemente fuera de negocio — bien-vs-servicio lo decide
    // UnspscEstado (ver ClasificadorUnspsc), no una palabra.
    //
    // Riesgo aceptado a propósito, no un detalle neutro: es la única etapa
    // donde un servicio legítimo puede morir por una sola palabra en el
    // nombre, sin dejar rastro en ninguna de las tres listas — UNSPSC nunca
    // llega a opinar sobre lo que descarta_duro ya mató. El objetivo
    // declarado es seguir achicando esta lista a medida que el presupuesto
    // de enriquecimiento lo permita (ver el filtro de acumulados en
    // Program.cs y sus ahorros medidos contra datos reales de producción).
    [property: JsonPropertyName("descarte_duro")] List<string> DescarteDuro,
    // Tipos de licitación privada (CO/B2/E2/H2/I2, 2026-09-09): tienen ciclo de
    // vida real (ventana de postulación, no solo aviso de transparencia),
    // pero algunos cierran el mismo día en que aparecen — todavía no hay
    // suficiente comprensión del patrón para dejarlos competir por Lista A.
    // Van siempre a Secundarias, sin pasar por clasificación de rubro (ver
    // FiltroLicitaciones), marcados con Candidata.TipoPrivado para poder
    // encontrarlos y revisar si promoverlos. Deben estar también en Tipos
    // para ser aceptados en la etapa 2 — TiposPrivados solo decide su
    // destino, no reemplaza esa validación.
    [property: JsonPropertyName("tipos_privados")] List<string> TiposPrivados,
    // Familias UNSPSC donde la clase de 8 dígitos no alcanza para decidir
    // bien-vs-servicio (F2, 2026-09-17): verificado con datos reales que
    // la familia 4323 (Software) mezcla arriendo de plataforma con
    // soporte (prospecto real) y compra pura de licencias (nunca un
    // prospecto) bajo la misma clase — UNSPSC.ResolverRaiz no puede
    // distinguirlos. Prefijos de CodigoProducto, no necesariamente de 4
    // dígitos: si aparece evidencia de que la ambigüedad es más angosta
    // (una clase de 6 dígitos específica), se puede acotar sin tocar
    // código. Ver ClasificadorUnspsc y UnspscEstado.RevisionManual.
    [property: JsonPropertyName("familias_unspsc_revision_manual")] List<string> FamiliasUnspscRevisionManual);
