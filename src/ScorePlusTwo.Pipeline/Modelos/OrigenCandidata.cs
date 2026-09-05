namespace ScorePlusTwo.Pipeline.Modelos;

// De qué barrido salió la candidata: del lote diario normal, o del barrido
// semanal `estado=activas` (candidatas que el lote diario no habría detectado
// porque no tuvieron movimiento el día de la corrida).
public enum OrigenCandidata
{
    Diario,
    Activas,
}
