using ScorePlusTwo.Pipeline.Api;
using ScorePlusTwo.Pipeline.Modelos;

namespace ScorePlusTwo.Pipeline.Enriquecimiento;

// Impuro: hace llamadas de red por cada CodigoExterno sin cache. Un fallo
// puntual (agotados los reintentos de MercadoPublicoClient) NUNCA es fatal
// para el resto del enriquecimiento: se registra por Console.Error y ese
// código queda sin entrada en el cache, así que se reintenta solo, gratis,
// en la próxima corrida — sigue siendo un cache-miss, no hace falta
// ninguna lógica de reintento adicional. Mismo principio de asimetría que
// ya existe para el barrido `activas` (Program.cs), aplicado a nivel de
// código individual en vez de barrido completo.
public static class EnriquecimientoUnspscService
{
    public static async Task<List<EntradaCacheUnspsc>> EnriquecerAsync(
        MercadoPublicoClient cliente, IReadOnlyList<string> codigosFaltantes, CancellationToken ct = default)
    {
        var nuevas = new List<EntradaCacheUnspsc>();

        foreach (var codigo in codigosFaltantes)
        {
            try
            {
                var detalle = await cliente.ObtenerDetalleAsync(codigo, ct);
                var licitacion = detalle.Listado.FirstOrDefault(l => l.CodigoExterno == codigo);
                var items = (licitacion?.Items?.Listado ?? new List<DetalleItem>())
                    .Select(i => new ItemUnspscCache(i.CodigoProducto, i.CodigoCategoria))
                    .ToList();

                nuevas.Add(new EntradaCacheUnspsc(codigo, items, DateTime.UtcNow, licitacion?.Comprador?.RegionUnidad));
            }
            catch (MercadoPublicoApiException ex)
            {
                Console.Error.WriteLine(
                    $"[ADVERTENCIA] Enriquecimiento UNSPSC omitido para {codigo}, se reintenta la próxima corrida: {ex.Message}");
            }
        }

        return nuevas;
    }
}
