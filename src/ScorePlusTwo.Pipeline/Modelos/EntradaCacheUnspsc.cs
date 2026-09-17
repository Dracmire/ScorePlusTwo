namespace ScorePlusTwo.Pipeline.Modelos;

// Un ítem del detalle de una licitación (Items.Listado[] de la API real).
// Una licitación puede traer varios ítems con CodigoProducto distinto cada
// uno — el caso real que motivó este rediseño ("Convenio Suministro de
// Servicio de Fotocopiado ... con Entrega de Equipos", 1057548-21-LE26) es
// exactamente un servicio con un ítem de bien mezclado adentro. Cachear
// solo el primer ítem perdería ese caso.
public sealed record ItemUnspscCache(int? CodigoProducto, string? CodigoCategoria);

// data/cache-unspsc.json: el hecho crudo de qué devolvió el detalle de la
// API para un CodigoExterno, nunca la raíz ya resuelta. El código UNSPSC
// que declaró el comprador no cambia una vez publicado, así que este
// registro es permanente — no se invalida ni se refresca. La resolución
// bien/servicio se recalcula en cada corrida caminando el catálogo sobre
// estos CodigoProducto (ver ClasificadorUnspsc): si el catálogo se corrige
// más adelante, lo que hoy cae en SinResolver se reclasifica solo la
// próxima corrida, sin gastar una llamada de API de nuevo. RegionUnidad
// (F2, 2026-09-17) viene gratis en el mismo detalle (Comprador.RegionUnidad)
// — mismo principio de permanencia, la región del comprador no cambia.
public sealed record EntradaCacheUnspsc(
    string CodigoExterno, List<ItemUnspscCache> Items, DateTime ObtenidoEn, string? RegionUnidad = null);
