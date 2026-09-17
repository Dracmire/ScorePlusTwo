using System.Globalization;
using ScorePlusTwo.Pipeline.Modelos;

namespace ScorePlusTwo.Pipeline.Unspsc;

// Función pura: dado lo que ya se sabe de una licitación (su entrada de
// cache, o su ausencia), el catálogo cargado en memoria, y las familias
// UNSPSC consideradas ambiguas en modalidad (ver Criterios.
// FamiliasUnspscRevisionManual), decide UnspscEstado. Nunca hace I/O — el
// cache y el catálogo se cargan afuera (Program.cs) y se pasan como
// argumento, igual que Criterios hoy.
public static class ClasificadorUnspsc
{
    // Precedencia: Servicio > RevisionManual > Bien > SinResolver. Un ítem
    // de familia ambigua marca RevisionManual salvo que otro ítem de la
    // misma licitación resuelva a raíz J (ahí gana Servicio, mismo
    // principio que "cualquier ítem de servicio basta").
    public static UnspscEstado Clasificar(
        EntradaCacheUnspsc? entrada, CatalogoUnspsc catalogo, IReadOnlySet<string> familiasRevisionManual)
    {
        if (entrada is null)
        {
            return UnspscEstado.PendienteEnriquecimiento;
        }

        var huboAlMenosUnaRaiz = false;
        var huboFamiliaAmbigua = false;

        foreach (var item in entrada.Items)
        {
            if (item.CodigoProducto is not { } codigoProducto)
            {
                continue;
            }

            if (EsFamiliaAmbigua(codigoProducto, familiasRevisionManual))
            {
                huboFamiliaAmbigua = true;
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

        if (huboFamiliaAmbigua)
        {
            return UnspscEstado.RevisionManual;
        }

        return huboAlMenosUnaRaiz ? UnspscEstado.Bien : UnspscEstado.SinResolver;
    }

    // Chequeo de prefijo sobre el propio CodigoProducto (ej. familia "4323"
    // sobre 43231512) — independiente de si el catálogo logra resolver la
    // raíz, corre en paralelo al recorrido de raíces, no lo reemplaza.
    private static bool EsFamiliaAmbigua(int codigoProducto, IReadOnlySet<string> familiasRevisionManual)
    {
        if (familiasRevisionManual.Count == 0)
        {
            return false;
        }

        var codigoTexto = codigoProducto.ToString(CultureInfo.InvariantCulture);
        foreach (var familia in familiasRevisionManual)
        {
            if (codigoTexto.StartsWith(familia, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
