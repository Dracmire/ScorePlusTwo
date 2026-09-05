namespace ScorePlusTwo.Pipeline.Filtro;

public static class CodigoExternoParser
{
    // El tipo NO viene en la API — se extrae del sufijo de CodigoExterno tras
    // el último guion, menos los últimos 2 caracteres (año de 2 dígitos).
    // Ej. "2981-256-LE26" -> tipo "LE", año "26". Regla verificada contra los
    // 1.172 registros del fixture real, sin excepciones. Try* deliberado: un
    // código con formato inesperado no debe crashear el lote completo.
    public static bool TryExtraerTipoAnio(string codigoExterno, out string tipo, out string anio)
    {
        tipo = string.Empty;
        anio = string.Empty;

        var indiceGuion = codigoExterno.LastIndexOf('-');
        if (indiceGuion < 0)
        {
            return false;
        }

        var sufijo = codigoExterno[(indiceGuion + 1)..];
        if (sufijo.Length <= 2)
        {
            return false;
        }

        tipo = sufijo[..^2];
        anio = sufijo[^2..];
        return true;
    }
}
