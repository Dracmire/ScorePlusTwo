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
    // y L1 por igual.
    [property: JsonPropertyName("descarte_duro")] List<string> DescarteDuro,
    // Distingue compra de bien vs. servicio (ej. "software", "servidor"):
    // ya no mata el registro, solo le impide entrar a Lista A cuando matchea
    // un rubro de prioridad alta — cae a Lista B en vez de desaparecer.
    [property: JsonPropertyName("exclusiones_rubro")] List<string> ExclusionesRubro,
    // Tipos de licitación privada (CO/B2/E2/I2, 2026-09-09): tienen ciclo de
    // vida real (ventana de postulación, no solo aviso de transparencia),
    // pero algunos cierran el mismo día en que aparecen — todavía no hay
    // suficiente comprensión del patrón para dejarlos competir por Lista A.
    // Van siempre a Secundarias, sin pasar por clasificación de rubro (ver
    // FiltroLicitaciones), marcados con Candidata.TipoPrivado para poder
    // encontrarlos y revisar si promoverlos. Deben estar también en Tipos
    // para ser aceptados en la etapa 2 — TiposPrivados solo decide su
    // destino, no reemplaza esa validación.
    [property: JsonPropertyName("tipos_privados")] List<string> TiposPrivados);
