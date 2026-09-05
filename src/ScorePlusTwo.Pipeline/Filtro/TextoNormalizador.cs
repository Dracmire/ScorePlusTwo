using System.Globalization;
using System.Text;

namespace ScorePlusTwo.Pipeline.Filtro;

public static class TextoNormalizador
{
    // Minúsculas + sin tildes/diacríticos, para comparar Nombre contra
    // términos de rubro/exclusión por substring. Nota: NFKD descompone "ñ" en
    // "n" + tilde combinante, que luego se elimina, así que esto también
    // colapsa "ñ"→"n". Con los términos actuales de config/criterios.json no
    // genera falsos positivos conocidos; es una simplificación intencional de
    // F1, revisable en F3 si aparece un caso real.
    public static string Normalizar(string texto)
    {
        var descompuesto = texto.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sinMarcas = new StringBuilder(descompuesto.Length);
        foreach (var c in descompuesto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sinMarcas.Append(c);
            }
        }

        return sinMarcas.ToString().Normalize(NormalizationForm.FormC);
    }
}
