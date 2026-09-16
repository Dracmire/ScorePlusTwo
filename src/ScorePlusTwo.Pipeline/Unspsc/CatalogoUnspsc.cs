using System.Globalization;

namespace ScorePlusTwo.Pipeline.Unspsc;

// Fila cruda de config/catalogo-unspsc.tsv (13.335 filas, exportado del
// Excel UNGM/UNSPSC que aportó el usuario). TSV, no CSV: varios títulos
// traen comas. Code es el código UNSPSC real (ej. "80111504" o la raíz de
// un solo caracter "A".."J"); Key/ParentKey son IDs internos del export,
// sin relación numérica con Code — la jerarquía se camina por ParentKey,
// nunca se infiere del propio Code.
internal readonly record struct FilaCatalogo(long Key, long? ParentKey, string Code);

// Camina Parent key hasta la raíz sin asumir profundidad fija: 80% de las
// filas resuelve en 4 saltos, pero hay casos verificados a mano entre 2 y 6
// — cualquier resolución debe caminar hasta ParentKey nulo, nunca detenerse
// a una profundidad fija. 18 filas (segmento 57, "emergency/humanitarian")
// tienen la cadena rota y no llegan a ninguna de las 10 raíces A-J:
// ResolverRaiz devuelve null para esos casos, nunca lanza ni asume un
// valor por defecto — el llamador decide qué hacer con "no resuelto"
// (ver UnspscEstado.SinResolver).
//
// 17 códigos aparecen duplicados en el catálogo fuente (mismo Code, Key
// distinto) — verificado a mano que las 17 duplicaciones resuelven a la
// misma raíz sin importar cuál copia se use, así que quedarse con la
// última leída al construir el índice por Code es seguro.
public sealed class CatalogoUnspsc
{
    // Ninguna cadena real observada supera 6 saltos; margen amplio contra
    // un ciclo no detectado en el archivo fuente.
    private const int TopeSaltos = 20;

    private static readonly HashSet<string> Raices = new() { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J" };

    private readonly Dictionary<string, FilaCatalogo> _porCodigo;
    private readonly Dictionary<long, FilaCatalogo> _porKey;
    private readonly Dictionary<int, char?> _cacheResolucion = new();

    private CatalogoUnspsc(Dictionary<string, FilaCatalogo> porCodigo, Dictionary<long, FilaCatalogo> porKey)
    {
        _porCodigo = porCodigo;
        _porKey = porKey;
    }

    public static CatalogoUnspsc CargarDesdeArchivo(string ruta) => CargarDesdeLineas(File.ReadLines(ruta));

    // Para tests: un catálogo minúsculo en memoria, mismo formato TSV
    // (encabezado + filas), sin tocar disco. No requiere datos reales —
    // sirve para probar la mecánica de caminar Parent key, no para
    // verificar contra la realidad (eso lo hace CatalogoUnspscTests contra
    // el archivo de producción).
    public static CatalogoUnspsc CargarDesdeTexto(string contenidoTsv) =>
        CargarDesdeLineas(contenidoTsv.Split('\n'));

    private static CatalogoUnspsc CargarDesdeLineas(IEnumerable<string> lineas)
    {
        var porCodigo = new Dictionary<string, FilaCatalogo>();
        var porKey = new Dictionary<long, FilaCatalogo>();

        foreach (var linea in lineas.Skip(1)) // encabezado: Key\tParentKey\tCode\tTitulo
        {
            if (linea.Length == 0)
            {
                continue;
            }

            var campos = linea.Split('\t');
            var key = long.Parse(campos[0], CultureInfo.InvariantCulture);
            long? parentKey = campos[1].Length == 0 ? null : long.Parse(campos[1], CultureInfo.InvariantCulture);
            var code = campos[2];

            var fila = new FilaCatalogo(key, parentKey, code);
            porKey[key] = fila;
            porCodigo[code] = fila;
        }

        return new CatalogoUnspsc(porCodigo, porKey);
    }

    // Devuelve la raíz (A-J) de un CodigoProducto UNSPSC (ej. 80111504 -> 'J'),
    // o null si el código no está en el catálogo o su cadena de ParentKey no
    // termina en una raíz conocida.
    public char? ResolverRaiz(int codigoProducto)
    {
        if (_cacheResolucion.TryGetValue(codigoProducto, out var cacheado))
        {
            return cacheado;
        }

        var resultado = ResolverSinCache(codigoProducto);
        _cacheResolucion[codigoProducto] = resultado;
        return resultado;
    }

    private char? ResolverSinCache(int codigoProducto)
    {
        var codigoTexto = codigoProducto.ToString(CultureInfo.InvariantCulture);
        if (!_porCodigo.TryGetValue(codigoTexto, out var actual))
        {
            return null;
        }

        var visitados = new HashSet<long>();
        var saltos = 0;

        while (true)
        {
            if (!visitados.Add(actual.Key))
            {
                return null; // ciclo — no observado en el archivo fuente, protección de todas formas.
            }

            if (actual.ParentKey is null)
            {
                return actual.Code.Length == 1 && Raices.Contains(actual.Code) ? actual.Code[0] : null;
            }

            if (saltos++ >= TopeSaltos || !_porKey.TryGetValue(actual.ParentKey.Value, out var siguiente))
            {
                return null; // cadena rota: ParentKey apunta a una fila que no existe en el catálogo.
            }

            actual = siguiente;
        }
    }
}
