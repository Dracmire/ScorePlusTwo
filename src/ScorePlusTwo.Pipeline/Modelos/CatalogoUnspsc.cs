using System.Globalization;

namespace ScorePlusTwo.Pipeline.Modelos;

// Carga temporal del catálogo UNGM/UNSPSC (13.335 filas, jerárquico por
// parent_key, exportado a TSV desde el Excel que aportó el usuario — ver
// plan de sesión, "Investigación — rediseño de la etapa 3 con UNSPSC").
// TSV en vez de CSV porque varios títulos contienen comas ("Servicios
// profesionales, administrativos...") y un split ingenuo las rompería;
// verificado que ningún título contiene tabs. Descartar junto con
// data/unspsc-catalogo-temporal.csv y el resto del experimento.
public sealed class CatalogoUnspsc
{
    private readonly Dictionary<long, (long? ParentKey, string Code, string Titulo)> _porKey;
    private readonly Dictionary<string, long> _keyPorCode;

    private CatalogoUnspsc(
        Dictionary<long, (long?, string, string)> porKey, Dictionary<string, long> keyPorCode)
    {
        _porKey = porKey;
        _keyPorCode = keyPorCode;
    }

    public static CatalogoUnspsc Cargar(string rutaTsv)
    {
        var porKey = new Dictionary<long, (long?, string, string)>();
        var keyPorCode = new Dictionary<string, long>();

        foreach (var linea in File.ReadLines(rutaTsv).Skip(1))
        {
            var campos = linea.Split('\t', 4);
            if (campos.Length < 4)
            {
                continue;
            }

            var key = long.Parse(campos[0], CultureInfo.InvariantCulture);
            long? parentKey = string.IsNullOrEmpty(campos[1])
                ? null
                : long.Parse(campos[1], CultureInfo.InvariantCulture);
            var code = campos[2];
            var titulo = campos[3];

            porKey[key] = (parentKey, code, titulo);
            keyPorCode[code] = key;
        }

        return new CatalogoUnspsc(porKey, keyPorCode);
    }

    // Camina Parent key hasta que sea null (raíz) — NUNCA asume una
    // profundidad fija (ver hallazgo: 80% resuelve en 4 saltos pero hay
    // filas a 2, 3, 5 y 6). Devuelve (Code, Titulo) de la raíz, o
    // (null, motivo) si el código no está en el catálogo o la cadena está
    // rota (18 filas del segmento 57 "emergency" no llegan a ninguna raíz
    // en este extracto).
    public (string? Raiz, string Titulo) ResolverRaiz(long codigoProducto)
    {
        if (!_keyPorCode.TryGetValue(codigoProducto.ToString(CultureInfo.InvariantCulture), out var key))
        {
            return (null, "codigo no encontrado en catalogo");
        }

        var visitados = new HashSet<long>();
        while (true)
        {
            if (!visitados.Add(key))
            {
                return (null, "ciclo detectado en el catalogo");
            }

            var (parentKey, code, titulo) = _porKey[key];
            if (parentKey is null)
            {
                return (code, titulo);
            }

            if (!_porKey.ContainsKey(parentKey.Value))
            {
                return (null, $"cadena rota en key={parentKey}");
            }

            key = parentKey.Value;
        }
    }
}
