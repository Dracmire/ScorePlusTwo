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
    public EstadoFlujo EstadoFlujo { get; set; } = EstadoFlujo.Pendiente;
    public string? Notas { get; set; }
    public string? ClienteAsignado { get; set; }
    public OrigenCandidata Origen { get; set; } = OrigenCandidata.Diario;
}
