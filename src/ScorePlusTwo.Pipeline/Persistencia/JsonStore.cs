using System.Text.Json;

namespace ScorePlusTwo.Pipeline.Persistencia;

public static class JsonStore
{
    public static T Cargar<T>(string ruta, JsonSerializerOptions opciones)
    {
        var json = File.ReadAllText(ruta);
        return JsonSerializer.Deserialize<T>(json, opciones)
            ?? throw new InvalidOperationException($"El archivo {ruta} deserializó a null.");
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
