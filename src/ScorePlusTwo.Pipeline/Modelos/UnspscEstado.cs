namespace ScorePlusTwo.Pipeline.Modelos;

// Reemplaza la palabra como filtro de entrada para bien-vs-servicio: la
// decide la categoría UNSPSC que el propio comprador declaró (ver
// ClasificadorUnspsc), no un match de "software"/"servidor" en el nombre.
// Servicio es el único estado que entra a clasificación de rubro
// (compliance/ti/ia) — Bien/SinResolver/PendienteEnriquecimiento van
// directo a Secundarias, nunca se destruyen ni se promueven a Prioritarias
// sin que UNSPSC haya confirmado servicio.
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
    Servicio,
}
