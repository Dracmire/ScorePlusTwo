using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScorePlusTwo.Pipeline.Persistencia;

public static class JsonOpciones
{
    // Respuestas de la API real / fixtures: campos PascalCase tal cual los
    // envía ChileCompra.
    public static readonly JsonSerializerOptions ApiLectura = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // config/criterios.json: palabras simples en minúscula.
    public static readonly JsonSerializerOptions Config = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // data/candidatas.json, data/informes.json, data/eventos.json: snake_case
    // multi-palabra, legible/editable a mano por triage humano.
    public static readonly JsonSerializerOptions Persistencia = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
