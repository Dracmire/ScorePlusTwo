namespace ScorePlusTwo.Pipeline.Modelos;

public sealed record ListadoLicitacionesResponse(
    int Cantidad,
    string FechaCreacion,
    string Version,
    List<LicitacionRaw> Listado);
