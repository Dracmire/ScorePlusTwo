namespace ScorePlusTwo.Pipeline.Modelos;

// Lista destino que un humano decidió para un CodigoExterno, vía el
// tablero (pestaña "Revisión", ver docs/app.js). Nunca TramoBajo — los
// overrides nacen de la cola de revisión de Secundarias
// (UnspscEstado.RevisionManual / EstadoFlujo.RevisionAmbigua), que nunca
// incluye tipo L1.
public enum ListaDestino
{
    Prioritarias,
    Secundarias,
}

// data/overrides.json: Dictionary<CodigoExterno, EntradaOverride>. Un
// override NUNCA expira ni se recalcula — es una decisión humana, no un
// hecho que UNSPSC/región/rubro puedan volver a evaluar. El pipeline lo
// aplica en cada corrida, forzando el CodigoExterno al destino indicado
// sin importar qué diga la clasificación automática (ver
// Filtro/AplicadorOverrides.cs). Revisado siempre viene en true desde el
// tablero — ambos botones ("Mover a Prioritarias" / "Confirmar en
// Secundarias") escriben un override, incluso el que no cambia de lista,
// para que la fila no vuelva a aparecer en la cola de revisión.
public sealed record EntradaOverride(ListaDestino ListaDestino, bool Revisado, DateTime ObservadoEn);
