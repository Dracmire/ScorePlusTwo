namespace ScorePlusTwo.Pipeline.Modelos;

// Reemplaza la palabra como filtro de entrada para bien-vs-servicio: la
// decide la categoría UNSPSC que el propio comprador declaró (ver
// ClasificadorUnspsc), no un match de "software"/"servidor" en el nombre.
// Servicio y RevisionManual son los únicos estados que entran a
// clasificación de rubro (compliance/ti/ia) — Bien/SinResolver/
// PendienteEnriquecimiento van directo a Secundarias, nunca se destruyen
// ni se promueven a Prioritarias sin que UNSPSC haya confirmado servicio.
public enum UnspscEstado
{
    // Sin entrada de cache todavía para este CodigoExterno: no se ha podido
    // consultar el detalle (primera vez que se ve, o el enriquecimiento
    // falló y se reintentará solo en la próxima corrida).
    PendienteEnriquecimiento,
    // Se consultó el detalle pero ningún Item resuelve a una raíz conocida
    // (CodigoProducto vacío, no encontrado en el catálogo, o cadena de
    // Parent key rota en todos los items) — nunca un fallback silencioso,
    // queda visible como bucket propio.
    SinResolver,
    Bien,
    // UNSPSC resuelve bien QUÉ se compra, pero no bajo qué modalidad: la
    // familia 4323 (Software) mezcla arriendo de plataforma con soporte
    // continuo (prospecto real) y compra pura de licencias de terceros
    // (nunca un prospecto) bajo la misma clase de 8 dígitos — verificado
    // con datos reales (1305541-3-LE26 y 598-20-LE26 comparten 43231500).
    // Nunca se auto-promueve a Prioritarias; sí evalúa rubro (igual que
    // Servicio) para darle contexto al humano que la revisa. Las familias
    // consideradas ambiguas viven en Criterios.FamiliasUnspscRevisionManual,
    // editable sin tocar código.
    RevisionManual,
    Servicio,
}

