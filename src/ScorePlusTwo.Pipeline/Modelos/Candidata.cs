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
    public required string RubroMatch { get; set; }
    public required string TerminoMatch { get; set; }
    public string? Region { get; set; }
    public string? Organismo { get; set; }

    // Moneda y Monto se guardan por separado y SIN CONVERTIR: el listado
    // diario no trae ninguno de los dos (F2 los resuelve vía detalle de
    // sobrevivientes), pero ya se vio en producción que un mismo lote mezcla
    // CLP, CLF (UF) y USD (ej. 548874-77-LR26 = 11.000 UF, 548874-74-LR26 =
    // USD 565.250). Convertir a un solo número sin la moneda haría que UF y
    // USD parezcan pesos y un filtro de banda descartaría licitaciones
    // grandes por error. Quedan null hasta que F2 implemente el detalle.
    public string? Moneda { get; set; }
    public decimal? Monto { get; set; }

    public EstadoFlujo EstadoFlujo { get; set; } = EstadoFlujo.Pendiente;
    public string? Notas { get; set; }
    public string? ClienteAsignado { get; set; }
    public OrigenCandidata Origen { get; set; } = OrigenCandidata.Diario;

    // Indicador de comportamiento de pago del comprador (ej. Renca: 16
    // reclamos en 12 meses; JUNAEB: 6) — criterio de exclusión de primer
    // orden en el modelo de negocio. Campo reservado para cuando F2
    // implemente el detalle de sobrevivientes; no se captura todavía.
    public int? CantidadReclamos { get; set; }
}
