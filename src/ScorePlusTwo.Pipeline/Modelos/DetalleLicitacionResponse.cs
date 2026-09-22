namespace ScorePlusTwo.Pipeline.Modelos;

// Shape mínimo del detalle de una licitación
// (licitaciones.json?codigo=...&ticket=...), permanente desde F2 — a
// diferencia del shape temporal del experimento UNSPSC, este solo trae lo
// que EnriquecimientoUnspscService necesita. Mismo envelope
// {Cantidad, FechaCreacion, Version, Listado} que el lote diario/adjudicada,
// verificado contra la API real durante la investigación de la etapa 3
// (598-16-LE26): Items.Listado[].CodigoProducto/CodigoCategoria trae el
// código UNSPSC v7 y su categoría cruda. Comprador.RegionUnidad (F2,
// 2026-09-17) resuelve la región de forma gratuita, en el mismo detalle
// que ya se pide para UNSPSC — sin depender de ningún cache de organismos.
//
// Moneda/VisibilidadMonto/MontoEstimado/CantidadReclamos (2026-09-18) son
// root-level, al mismo nivel que Comprador — verificado con JSON real de los
// 9 códigos del gate (ej. 4956-74-LE26: MontoEstimado=21520000.0,
// Moneda="CLP", VisibilidadMonto=1; 3797-48-LE26/85-41-LE26: MontoEstimado=
// null, VisibilidadMonto=0 cuando el organismo no publica el monto).
// CantidadReclamos también root-level, valores reales verificados de 5 a
// 11.860 según el organismo — capturado aquí pero deliberadamente no usado
// como filtro todavía (ver EntradaCacheUnspsc/Candidata): no comparable
// entre organismos de distinto tamaño sin normalizar por volumen de compras.
public sealed record DetalleLicitacionResponse(
    int Cantidad, string FechaCreacion, string Version, List<DetalleLicitacion> Listado);

// CodigoEstado (2026-09-21): root-level, mismo nombre y significado que
// LicitacionRaw.CodigoEstado en el listado — 5 es Publicada. Lo que
// permite a --reevaluar-inventario traer Estado y los ítems UNSPSC en la
// misma llamada de detalle, sin pedir dos veces.
//
// Descripcion/ProhibicionContratacion/TipoPago/SubContratacion (2026-09-22),
// root-level, verificados contra 2 códigos reales (2741-60-LE26,
// 1000813-15-LE26) antes de modelarlos:
// - Descripcion: texto libre completo (más largo que Nombre).
// - ProhibicionContratacion: texto libre (ej. "Art. 11.4 de las Bases
//   Administrativas") o "" cuando no aplica — NO es booleano, pese al
//   nombre.
// - TipoPago/SubContratacion: códigos numéricos como string ("4", "1",
//   "0" en los dos casos reales) SIN diccionario de traducción
//   disponible — se capturan y persisten tal cual, crudos, y quedan
//   ocultos por default en el panel del tablero (ver
//   config/panel-revision.json) hasta que se consiga el diccionario
//   oficial de ChileCompra que los traduce a texto legible.
//
// Fechas.FechaCierre (2026-09-22): el FechaCierre root-level de este
// endpoint viene SIEMPRE null en los códigos verificados — el valor real
// vive acá (confirmado ya en 2026-09-14 y de nuevo ahora). Solo se lee
// para refrescar Candidata.FechaCierre vía --refrescar-descriptivos
// (Program.cs) — el flujo diario normal sigue usando
// LicitacionRaw.FechaCierre del listado, sin cambios.
//
// Tiempo/UnidadTiempo/TiempoDuracionContrato/UnidadTiempoDuracionContrato/
// TipoDuracionContrato: verificados como existentes en la API (3 grupos de
// campos distintos para "duración"), pero deliberadamente NO modelados en
// esta ronda — ninguno de los 2 códigos reales tenía valor no-trivial, no
// hay forma de confirmar cuál representa "duración del contrato" sin un
// tercer caso con datos reales. Pendiente para una ronda futura si hace
// falta.
public sealed record DetalleLicitacion(
    string CodigoExterno,
    DetalleItems? Items,
    DetalleComprador? Comprador,
    string? Moneda,
    int? VisibilidadMonto,
    decimal? MontoEstimado,
    int? CantidadReclamos,
    int CodigoEstado,
    DetalleAdjudicacion? Adjudicacion = null,
    string? Descripcion = null,
    string? ProhibicionContratacion = null,
    string? TipoPago = null,
    string? SubContratacion = null,
    DetalleFechas? Fechas = null);

// Adjudicacion a nivel de licitación (2026-09-21, tipado 2026-09-22 tras
// verificar el shape real contra 1000813-15-LE26): metadata del acta —
// NUNCA trae el ganador, solo cuándo/cómo se adjudicó y un link a la
// ficha real en mercadopublico.cl. El ganador vive en
// DetalleItem.Adjudicacion (por ítem), ver abajo — hallazgo del
// 2026-09-22 que corrige la conclusión anterior ("no sirve para ver
// ganadores"), que solo había mirado este nivel.
public sealed record DetalleAdjudicacion(
    int? Tipo, string? Fecha, string? Numero, int? NumeroOferentes, string? UrlActa);

// FechaCierre real del detalle (root-level FechaCierre viene siempre
// null, ver comentario de DetalleLicitacion) — DateTime? deserializa
// nativo desde el string ISO de la API, mismo criterio que
// LicitacionRaw.FechaCierre.
public sealed record DetalleFechas(DateTime? FechaCierre);

public sealed record DetalleItems(List<DetalleItem> Listado);

// Categoria (2026-09-21): texto ya resuelto por la API con los niveles de
// la jerarquía UNSPSC separados por "/" (ej. "Servicios profesionales,
// administrativos y consultorías de gestión empresarial / Servicios de
// recursos humanos / Consultorías para el desarrollo de recursos humanos")
// — confirmado con datos reales en la investigación UNSPSC de septiembre,
// pero nunca capturado hasta ahora porque F2 no lo necesitaba (la raíz
// bien/servicio se resuelve caminando CodigoProducto contra el catálogo,
// no leyendo este texto). El tablero sí lo necesita para mostrar la
// descripción completa de cada ítem, no solo el rubro ya resuelto.
//
// Adjudicacion (2026-09-22): el ganador real vive ACÁ, por ítem, no en
// DetalleLicitacion.Adjudicacion (ver arriba) — verificado contra
// 1000813-15-LE26: {RutProveedor, NombreProveedor, Cantidad,
// MontoUnitario}. Null mientras la licitación no esté adjudicada. Solo
// se usa en la pestaña Consulta (ResultadoConsulta) — deliberadamente NO
// se propaga a ItemUnspscCache/Candidata: el inventario de
// Prioritarias/Secundarias/TramoBajo es de licitaciones en triage, no de
// seguimiento de adjudicaciones.
public sealed record DetalleItem(
    int? CodigoProducto, string? CodigoCategoria, string? Categoria, DetalleItemAdjudicacion? Adjudicacion = null);

public sealed record DetalleItemAdjudicacion(
    string? RutProveedor, string? NombreProveedor, decimal? Cantidad, decimal? MontoUnitario);

// La API real devuelve RegionUnidad con espacio final en al menos un caso
// verificado ("Región del Biobío ", código 732434-20-LP26) — el matching
// por substring de EsRegionElegible no se rompe con eso, pero si alguna
// vez se compara por igualdad exacta hay que Trim() primero.
//
// NombreOrganismo/ComunaUnidad (2026-09-22), verificados junto a
// RegionUnidad en el mismo objeto Comprador: NombreOrganismo puebla el
// campo Candidata.Organismo YA EXISTENTE (siempre null desde F1, nunca se
// había resuelto) — no se crea un campo redundante. ComunaUnidad sí es
// nuevo (Candidata.Comuna).
public sealed record DetalleComprador(string? RegionUnidad, string? NombreOrganismo = null, string? ComunaUnidad = null);
