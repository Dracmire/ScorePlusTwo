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
// Otros campos del detalle real (Categoria en texto, CantidadReclamos,
// Fechas.FechaCierre) no se modelan aquí porque F2 no los usa — quedan
// ignorados por PropertyNameCaseInsensitive, no producen error de
// deserialización.
public sealed record DetalleLicitacionResponse(
    int Cantidad, string FechaCreacion, string Version, List<DetalleLicitacion> Listado);

public sealed record DetalleLicitacion(string CodigoExterno, DetalleItems? Items, DetalleComprador? Comprador);

public sealed record DetalleItems(List<DetalleItem> Listado);

public sealed record DetalleItem(int? CodigoProducto, string? CodigoCategoria);

// La API real devuelve RegionUnidad con espacio final en al menos un caso
// verificado ("Región del Biobío ", código 732434-20-LP26) — el matching
// por substring de EsRegionElegible no se rompe con eso, pero si alguna
// vez se compara por igualdad exacta hay que Trim() primero.
public sealed record DetalleComprador(string? RegionUnidad);

