using ScorePlusTwo.Pipeline.Modelos;

namespace ScorePlusTwo.Pipeline.Filtro;

// Aplica data/overrides.json (decisiones humanas escritas desde el
// tablero, ver docs/app.js) sobre las listas ya fusionadas de
// Prioritarias/Secundarias, ANTES de que Program.cs las persista. Un
// override nunca expira: es una decisión humana, no un hecho recalculable
// por UNSPSC/región/rubro, así que se reaplica en cada corrida — si el
// código ya está en el destino correcto, es un no-op (idempotente por
// construcción, no necesita lógica especial para detectarlo). Nunca toca
// TramoBajo: los overrides nacen de la cola de revisión de Secundarias
// (UnspscEstado.RevisionManual / EstadoFlujo.RevisionAmbigua), que nunca
// incluye tipo L1.
//
// Función pura: no hace I/O — el llamador carga el diccionario de
// overrides y decide cuándo persistir el resultado.
public static class AplicadorOverrides
{
    public static (List<Candidata> Prioritarias, List<Candidata> Secundarias, bool Cambio) Aplicar(
        IReadOnlyDictionary<string, EntradaOverride> overrides,
        IReadOnlyList<Candidata> prioritarias,
        IReadOnlyList<Candidata> secundarias)
    {
        if (overrides.Count == 0)
        {
            return (prioritarias.ToList(), secundarias.ToList(), false);
        }

        var nuevasPrioritarias = new List<Candidata>();
        var nuevasSecundarias = new List<Candidata>();
        var cambio = false;

        foreach (var candidata in prioritarias)
        {
            if (overrides.TryGetValue(candidata.Codigo, out var over) && over.ListaDestino == ListaDestino.Secundarias)
            {
                nuevasSecundarias.Add(candidata);
                cambio = true;
            }
            else
            {
                nuevasPrioritarias.Add(candidata);
            }
        }

        foreach (var candidata in secundarias)
        {
            if (overrides.TryGetValue(candidata.Codigo, out var over) && over.ListaDestino == ListaDestino.Prioritarias)
            {
                nuevasPrioritarias.Add(candidata);
                cambio = true;
            }
            else
            {
                nuevasSecundarias.Add(candidata);
            }
        }

        return (nuevasPrioritarias, nuevasSecundarias, cambio);
    }
}
