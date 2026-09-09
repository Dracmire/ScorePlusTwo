namespace ScorePlusTwo.Pipeline.Modelos;

public enum EstadoFlujo
{
    Pendiente,
    Candidata,
    Scorecard,
    Enviada,
    Tomada,
    Descartada,
    // Asignados por la revalidación de estado (ver Program.RevalidarEstado),
    // no por triage humano: la licitación dejó de estar Publicada (5) en
    // Mercado Público. Mapean CodigoEstado 6/7/8 respectivamente.
    Cerrada,
    Desierta,
    Adjudicada,
}
