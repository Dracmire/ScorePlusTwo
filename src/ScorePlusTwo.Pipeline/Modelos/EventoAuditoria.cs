namespace ScorePlusTwo.Pipeline.Modelos;

public sealed record EventoAuditoria(
    DateTime Timestamp, string Actor, string Accion, string? Codigo, string? Detalle);
