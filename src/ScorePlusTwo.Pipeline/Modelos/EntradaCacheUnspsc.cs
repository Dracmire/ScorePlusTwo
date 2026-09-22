namespace ScorePlusTwo.Pipeline.Modelos;

// Un ítem del detalle de una licitación (Items.Listado[] de la API real).
// Una licitación puede traer varios ítems con CodigoProducto distinto cada
// uno — el caso real que motivó este rediseño ("Convenio Suministro de
// Servicio de Fotocopiado ... con Entrega de Equipos", 1057548-21-LE26) es
// exactamente un servicio con un ítem de bien mezclado adentro. Cachear
// solo el primer ítem perdería ese caso.
//
// Categoria (2026-09-21): texto de la jerarquía UNSPSC ya resuelto por la
// API (ver DetalleItem) — capturado para mostrar en el tablero la
// descripción completa de cada ítem, no solo CodigoCategoria (el código
// numérico crudo). Parámetro opcional al final para no romper los
// constructores posicionales existentes (tests, fixtures).
public sealed record ItemUnspscCache(int? CodigoProducto, string? CodigoCategoria, string? Categoria = null);

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
//
// Moneda/Monto/CantidadReclamos (2026-09-18) vienen del mismo detalle, sin
// llamada adicional. Moneda/Monto YA vienen normalizados al construir esta
// entrada (ver EnriquecimientoUnspscService): VisibilidadMonto==0 (monto no
// publicado) se guarda como null en ambos, nunca como 0 — para que el
// tablero muestre un guión en vez de un importe falso de cero pesos.
// CantidadReclamos se guarda tal cual, sin usarse como filtro todavía (ver
// Candidata.CantidadReclamos): valores reales de 5 a 11.860 no son
// comparables entre organismos de distinto tamaño sin normalizar por
// volumen de compras.
//
// Descripcion/NombreOrganismo/ComunaUnidad/FechaCierre/
// ProhibicionContratacion/TipoPago/SubContratacion (2026-09-22): mismo
// detalle, sin llamada adicional — verificados contra datos reales antes
// de agregarlos (ver DetalleLicitacionResponse.cs para el detalle de cada
// uno, incluida la advertencia de que TipoPago/SubContratacion son
// códigos sin diccionario de traducción disponible, y que FechaCierre acá
// es la del DETALLE, no la del listado diario — solo se usa para
// refrescar Candidata.FechaCierre vía --refrescar-descriptivos).
public sealed record EntradaCacheUnspsc(
    string CodigoExterno,
    List<ItemUnspscCache> Items,
    DateTime ObtenidoEn,
    string? RegionUnidad = null,
    string? Moneda = null,
    decimal? Monto = null,
    int? CantidadReclamos = null,
    string? Descripcion = null,
    string? NombreOrganismo = null,
    string? ComunaUnidad = null,
    DateTime? FechaCierre = null,
    string? ProhibicionContratacion = null,
    string? TipoPago = null,
    string? SubContratacion = null);
