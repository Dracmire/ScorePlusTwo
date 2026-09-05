namespace ScorePlusTwo.Pipeline.Modelos;

public sealed record RubroCriterio(string Id, bool Activo, List<string> Terminos);

public sealed record Criterios(
    string Version,
    List<string> Tipos,
    List<int> Estados,
    List<string> Regiones,
    List<RubroCriterio> Rubros,
    List<string> Exclusiones);
