using System.Text.Json;

namespace ScorePlusTwo.Pipeline.Persistencia;

public static class JsonStore
{
    public static T Cargar<T>(string ruta, JsonSerializerOptions opciones)
    {
        var json = File.ReadAllText(ruta);
        RechazarSiNoEsJson(ruta, json);
        return JsonSerializer.Deserialize<T>(json, opciones)
            ?? throw new InvalidOperationException($"El archivo {ruta} deserializó a null.");
    }

    // El pipeline SOLO lee JSON. El CSV que exporta Mercado Público usa ';'
    // como separador sin encomillar los campos que a su vez contienen ';'
    // embebido — un caso real (85-34-LP26) desalineó columnas en silencio
    // (Moneda salió "1", VisibilidadMonto salió "Ley de Presupuestos"). En
    // vez de dejar que un CSV llegue a JsonSerializer y falle con un
    // JsonException críptico (o, peor, que alguien intente parsearlo campo
    // por campo), se rechaza aquí con un mensaje explícito.
    private static void RechazarSiNoEsJson(string ruta, string contenido)
    {
        var inicio = contenido.AsSpan().TrimStart();
        if (inicio.Length > 0 && (inicio[0] == '{' || inicio[0] == '['))
        {
            return;
        }

        throw new InvalidOperationException(
            $"'{ruta}' no es JSON válido — este pipeline no parsea CSV. El CSV de Mercado " +
            "Público usa ';' sin encomillar campos que contienen ';' embebido y desalinea " +
            "columnas en silencio (ver caso 85-34-LP26). Convierte el archivo a JSON antes de usarlo.");
    }

    public static T CargarOPredeterminado<T>(string ruta, JsonSerializerOptions opciones, T predeterminado)
    {
        return File.Exists(ruta) ? Cargar<T>(ruta, opciones) : predeterminado;
    }

    public static void Guardar<T>(string ruta, T valor, JsonSerializerOptions opciones)
    {
        var directorio = Path.GetDirectoryName(ruta);
        if (!string.IsNullOrEmpty(directorio))
        {
            Directory.CreateDirectory(directorio);
        }

        var json = JsonSerializer.Serialize(valor, opciones);
        File.WriteAllText(ruta, json);
    }
}
