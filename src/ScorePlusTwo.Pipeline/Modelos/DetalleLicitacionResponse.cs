namespace ScorePlusTwo.Pipeline.Modelos;

// Shape MÍNIMO del endpoint de detalle (licitaciones.json?codigo=...),
// acotado a lo que necesita el experimento de clasificación UNSPSC (ver
// plan de sesión, "Investigación — rediseño de la etapa 3 con UNSPSC").
// No modela Fechas/CantidadReclamos/el resto de campos del detalle real
// (ver esos hallazgos documentados en el plan) porque este experimento no
// los usa. Descartar junto con el resto del experimento: cuando F2
// implemente el detalle de sobrevivientes en serio, este tipo se
// reemplaza por el shape completo, no se extiende.
public sealed record ItemDetalle(int? CodigoProducto, string? CodigoCategoria, string? Categoria);

public sealed record ItemsDetalle(List<ItemDetalle> Listado);

public sealed record LicitacionDetalleItem(string CodigoExterno, ItemsDetalle? Items);

public sealed record DetalleLicitacionResponse(List<LicitacionDetalleItem> Listado);
