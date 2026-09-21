namespace ScorePlusTwo.Pipeline.Modelos;

// Salida persistida de --consultar-licitacion (2026-09-21):
// data/consultas/{codigo}.json. Encontrado=false cuando el Listado de la
// respuesta viene vacío (código inexistente o mal escrito) — se persiste
// igual, así el tablero no vuelve a disparar el workflow por un código que
// ya sabe que no existe. Detalle es el DetalleLicitacion crudo tal cual lo
// devolvió la API, Adjudicacion incluido sin modelar (ver
// DetalleLicitacionResponse.cs).
public sealed record ResultadoConsulta(
    string CodigoExterno,
    DateTime ConsultadoEn,
    bool Encontrado,
    DetalleLicitacion? Detalle);
