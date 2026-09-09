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
    [property: JsonPropertyName("exclusiones_rubro")] List<string> ExclusionesRubro);
