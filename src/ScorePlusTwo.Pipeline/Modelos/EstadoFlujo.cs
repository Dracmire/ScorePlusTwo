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
    // Asignado por --reevaluar-inventario (2026-09-21), no al crear la
    // candidata sino al DEGRADARLA: una Prioritaria existente que, al
    // reclasificarse con las reglas actuales (UNSPSC + región + rubro con
    // términos ambiguos), deja de calificar como Servicio+región
    // elegible+rubro alta. Se mueve a Secundarias con este estado en vez
    // de uno genérico, para que la pestaña "Revisión" del tablero la
    // muestre igual que RevisionManual/RevisionAmbigua — un humano decide
    // si estaba bien degradarla (Confirmar en Secundarias) o si el
    // resultado automático se equivocó (Mover a Prioritarias, escribe un
    // override que gana sobre esta reclasificación).
    RevisionDegradada,
}
