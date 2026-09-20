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
    // Asignado al crear la candidata (2026-09-19, ver FiltroLicitaciones):
    // el único término de rubro que matchea está marcado como
    // "terminos_ambiguos" en criterios.json (ej. "plataforma" en ti) — sin
    // ese término, habría promovido a Prioritarias (rubro alta + región
    // elegible), pero una señal única y ambigua no alcanza por sí sola.
    // Nunca se recalcula después de creada: un humano la resuelve desde el
    // tablero (pestaña "Revisión"), lo que escribe un override en
    // data/overrides.json — ver Filtro/AplicadorOverrides.cs.
    RevisionAmbigua,
}
