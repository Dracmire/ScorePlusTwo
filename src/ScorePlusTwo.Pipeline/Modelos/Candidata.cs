namespace ScorePlusTwo.Pipeline.Modelos;

// Clase mutable (no record): se relee y se fusiona día a día, y en F2+ el
// triage humano edita campos como Notas/ClienteAsignado/EstadoFlujo in situ.
public sealed class Candidata
{
    public required string Codigo { get; set; }
    public required string Nombre { get; set; }
    public required string Tipo { get; set; }
    public DateTime? FechaCierre { get; set; }
    public DateOnly FechaLote { get; set; }
    // Null para tramo bajo (L1 no pasa por clasificación de rubro) y para
    // secundarias sin ningún rubro match (inventario crudo de prospección).
    public string? RubroMatch { get; set; }
    public string? TerminoMatch { get; set; }
    // Region se puebla desde EntradaCacheUnspsc.RegionUnidad (F2,
    // 2026-09-17) para toda candidata que se haya enriquecido — viene
    // gratis en el mismo detalle que ya se pide para UNSPSC, sin cache de
    // organismos. Null para lo que nunca se enriquece (TramoBajo, tipos
    // privados) o para candidatas de antes de este cambio.
    public string? Region { get; set; }
    // Organismo (2026-09-22): poblado desde NombreOrganismo del detalle,
    // mismo objeto Comprador que ya trae RegionUnidad — el campo existe
    // desde F1 pero quedó siempre null hasta ahora ("sin resolución de
    // organismo todavía"). Comuna es nuevo, mismo objeto/misma fuente.
    public string? Organismo { get; set; }
    public string? Comuna { get; set; }

    // Descripcion/ProhibicionContratacion/TipoPago/SubContratacion
    // (2026-09-22), mismo detalle de enriquecimiento, sin llamada
    // adicional — ver EntradaCacheUnspsc para el detalle de cada uno.
    // TipoPago/SubContratacion son códigos crudos sin diccionario de
    // traducción disponible (ver DetalleLicitacionResponse.cs) — se
    // guardan tal cual, el tablero los muestra ocultos por default.
    public string? Descripcion { get; set; }
    public string? ProhibicionContratacion { get; set; }
    public string? TipoPago { get; set; }
    public string? SubContratacion { get; set; }

    // "bajo" para tipo L1 (ver ResultadoFiltro.TramoBajo); null para el
    // resto. No se mezcla con Lista A/B — separa candidatas.json/
    // secundarias.json de tramo_bajo.json.
    public string? Tramo { get; set; }

    // true para tipos privados (CO/B2/E2/H2/I2, ver Criterios.TiposPrivados):
    // siempre van a Secundarias sin pasar por clasificación de rubro, aunque
    // tienen ciclo de vida real (2026-09-09). Es lo que permite encontrarlos
    // dentro de secundarias.json para revisar si promoverlos a Prioritarias.
    public bool TipoPrivado { get; set; }

    // Moneda y Monto se guardan por separado y SIN CONVERTIR: el listado
    // diario no trae ninguno de los dos, pero ya se vio en producción que un
    // mismo lote mezcla CLP, CLF (UF) y USD (ej. 548874-77-LR26 = 11.000 UF,
    // 548874-74-LR26 = USD 565.250). Convertir a un solo número sin la
    // moneda haría que UF y USD parezcan pesos y un filtro de banda
    // descartaría licitaciones grandes por error. Poblados desde el detalle
    // de enriquecimiento UNSPSC (2026-09-18, ver EntradaCacheUnspsc) — null
    // para lo que nunca se enriquece (TramoBajo, tipos privados) o cuando el
    // organismo no publicó el monto (VisibilidadMonto: 0 en la API real, ya
    // normalizado a null en el cache, nunca guardado como 0).
    public string? Moneda { get; set; }
    public decimal? Monto { get; set; }

    public EstadoFlujo EstadoFlujo { get; set; } = EstadoFlujo.Pendiente;
    public string? Notas { get; set; }
    public string? ClienteAsignado { get; set; }
    public OrigenCandidata Origen { get; set; } = OrigenCandidata.Diario;

    // Indicador de comportamiento del comprador — criterio de negocio de
    // primer orden, pero NO usado como filtro todavía (2026-09-18): valores
    // reales verificados van de 5 a 459 (PDI 459, Cauquenes 332, Aysén 244,
    // Concepción 219, JUNAEB 156, DIPRECA 54, Renca 16) y no son comparables
    // entre organismos de distinto tamaño sin normalizar antes por volumen
    // de compras — castigaría sistemáticamente a los organismos grandes.
    // Poblado desde el mismo detalle de enriquecimiento UNSPSC, sin llamada
    // adicional (ver EntradaCacheUnspsc). Null para lo que nunca se
    // enriquece.
    public int? CantidadReclamos { get; set; }

    // Bien-vs-servicio decidido por UNSPSC (ver ClasificadorUnspsc), no por
    // palabra. Default PendienteEnriquecimiento para TramoBajo/tipos
    // privados, que nunca se enriquecen (ver FiltroLicitaciones) — es un
    // valor honesto: nunca se intentó clasificarlos, no un error.
    public UnspscEstado UnspscEstado { get; set; } = UnspscEstado.PendienteEnriquecimiento;

    // CodigoProducto crudo de cada ítem del detalle (ver EntradaCacheUnspsc)
    // — para auditoría/depuración manual, no para volver a resolver la raíz
    // (eso ya lo hizo ClasificadorUnspsc antes de persistir la candidata).
    public List<int> CodigosProductoUnspsc { get; set; } = new();

    // Ítems completos del cache (CodigoProducto + Categoria en texto,
    // 2026-09-21) — CodigosProductoUnspsc arriba solo guarda los enteros;
    // el tablero necesita también la descripción de texto completa para el
    // detalle expandible de la pestaña Revisión. Mismo default vacío que
    // CodigosProductoUnspsc para lo que nunca se enriquece.
    public List<ItemUnspscCache> ItemsUnspsc { get; set; } = new();

    // UltimaVerificacion/EstadoMp/IntentosNoEncontrada/MotivoCierre
    // (2026-09-30, ver Verificacion/ReverificacionService.cs): el dedupe
    // (FusionarLista) nunca vuelve a tocar un código ya existente, y
    // RevalidarEstado solo detecta un cierre si el código reaparece en el
    // lote diario — una licitación que deja de tener "movimiento" en el
    // feed incremental queda congelada para siempre sin este mecanismo.
    // Estos 4 campos son el estado propio de esa re-verificación contra el
    // detalle real de la API, poblados/actualizados ÚNICAMENTE por
    // ReverificacionService — ninguna otra ruta de enriquecimiento los
    // toca en este cambio.

    // Último momento (hora de Chile, ver Program.AhoraChile) en que se
    // intentó re-verificar este código con éxito (encontrado o no
    // encontrado, nunca en un fallo de red) — null para candidatas nunca
    // re-verificadas. Permite que la corrida sea idempotente/reanudable
    // (SeleccionarTope salta lo ya verificado hoy) y que el tablero
    // muestre qué tan reciente es el dato.
    public DateTime? UltimaVerificacion { get; set; }

    // Último CodigoEstado crudo que devolvió la API para este código, en
    // una respuesta donde SÍ apareció en Listado — null para candidatas
    // nunca re-verificadas o cuya última re-verificación no lo encontró.
    // Hace visible en el tablero un cierre_detectado_en_triage sin tener
    // que leer data/eventos.json.
    public int? EstadoMp { get; set; }

    // Contador de llamadas consecutivas donde la API respondió
    // correctamente pero el código no apareció en Listado (distinto de un
    // fallo de red, ver ReverificacionService.ReverificarCandidatasAsync).
    // Se resetea a 0 en cuanto vuelve a encontrarse. Se cappea en 3 para
    // que la comparación de transición del evento no_encontrada_en_api
    // tenga sentido en un código triaged que nunca se mueve de lista.
    public int IntentosNoEncontrada { get; set; }

    // Motivo del último paso a un EstadoFlujo terminal — distingue un
    // cierre INFERIDO (código dejó de listarse, IntentosNoEncontrada llegó
    // a 3, nunca se observó un CodigoEstado de cierre real: valor
    // "no_encontrada_en_api") de uno CONFIRMADO por la API (null, el
    // default — cubre CodigoEstado 6/7/8/18/no documentado, y también lo
    // movido a histórico por RevalidarEstado/--reevaluar-inventario, que
    // nunca setean este campo). Usado únicamente por
    // FiltroLicitaciones.EsCodigoNuevo (el fix de reingreso de
    // FusionarLista): un cierre inferido no bloquea que el código
    // reingrese si vuelve a aparecer Publicada; uno confirmado sí. No se
    // expone en el tablero.
    public string? MotivoCierre { get; set; }
}
