using ScorePlusTwo.Pipeline.Modelos;

namespace ScorePlusTwo.Pipeline.Unspsc;

// Función pura: dado lo que ya se sabe de una licitación (su entrada de
// cache, o su ausencia) y el catálogo cargado en memoria, decide
// UnspscEstado. Nunca hace I/O — el cache y el catálogo se cargan afuera
// (Program.cs) y se pasan como argumento, igual que Criterios hoy.
public static class ClasificadorUnspsc
{
    public static UnspscEstado Clasificar(EntradaCacheUnspsc? entrada, CatalogoUnspsc catalogo)
    {
        if (entrada is null)
        {
            return UnspscEstado.PendienteEnriquecimiento;
        }

        var huboAlMenosUnaRaiz = false;

        foreach (var item in entrada.Items)
        {
            if (item.CodigoProducto is not { } codigoProducto)
            {
                continue;
            }

            var raiz = catalogo.ResolverRaiz(codigoProducto);
            if (raiz is null)
            {
                continue;
            }

            if (raiz == 'J')
            {
                // Cualquier ítem de servicio basta: una licitación con
                // bien + servicio mezclados (caso fotocopiado,
                // 1057548-21-LE26) es una oportunidad de servicio real.
                return UnspscEstado.Servicio;
            }

            huboAlMenosUnaRaiz = true;
        }

        return huboAlMenosUnaRaiz ? UnspscEstado.Bien : UnspscEstado.SinResolver;
    }
}
