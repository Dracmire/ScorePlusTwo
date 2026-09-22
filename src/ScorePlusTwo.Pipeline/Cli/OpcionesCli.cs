using System.Globalization;

namespace ScorePlusTwo.Pipeline.Cli;

// Parseo manual mínimo, sin dependencia de System.CommandLine.
//
// --refiltrar/--criterios/--desde/--salida son un modo aparte (ver
// RefiltradoService y Program.EjecutarRefiltrado): re-filtran todo
// data/raw/ con un criterios.json alternativo y escriben un CSV, sin tocar
// el flujo automático ni sus archivos de estado. --desde usa formato ISO
// (YYYY-MM-DD), distinto de --fecha (DD-MM-YYYY) — son flags separados que
// nunca se combinan, así que no hay ambigüedad de formato entre ellos.
//
// --backfill-unspsc (2026-09-18) es otro modo aparte (ver Program.
// EjecutarBackfillUnspscAsync): reclasifica de una sola vez las
// Prioritarias existentes que el filtro de acumulados congeló antes de que
// F2 (UNSPSC) existiera, moviendo a Secundarias las que no correspondan.
// Requiere MP_TICKET (sin equivalente a --fixture) y no se combina con
// ningún otro flag.
//
// --reevaluar-inventario (2026-09-21), mismo patrón que --backfill-unspsc
// (ver Program.EjecutarReevaluarInventarioAsync): mantenimiento puntual
// sobre Prioritarias + Tramo bajo (Secundarias queda intacto) — revalida
// Estado y reclasifica con las reglas actuales (términos ambiguos
// incluidos). Requiere MP_TICKET, sin --fixture, no se combina con otro
// flag.
//
// --consultar-licitacion <codigo> (2026-09-21), mismo patrón estructural
// que los dos anteriores (ver Program.EjecutarConsultarLicitacionAsync):
// una sola llamada de detalle sobre un código puntual, persistida en
// data/consultas/{codigo}.json — sirve de cache para el botón "Consultar"
// del tablero. Requiere MP_TICKET, sin --fixture.
//
// --refrescar-descriptivos (2026-09-22), mismo patrón estructural que los
// tres anteriores (ver Program.EjecutarRefrescarDescriptivosAsync):
// refresca campos descriptivos (Descripcion, Organismo, Comuna, Region,
// Moneda, Monto, CantidadReclamos, ItemsUnspsc, etc.) de Prioritarias
// completas + la cola de Revisión dentro de Secundarias — NUNCA
// reclasifica ni mueve de lista (a diferencia de --reevaluar-inventario,
// que sí lo hace). Requiere MP_TICKET, sin --fixture.
public sealed record OpcionesCli(
    string? RutaFixture,
    DateOnly? Fecha,
    bool Refiltrar,
    string? RutaCriteriosAlternativos,
    DateOnly? Desde,
    string? RutaSalidaRefiltrado,
    bool BackfillUnspsc,
    bool ReevaluarInventario,
    string? ConsultarLicitacion,
    bool RefrescarDescriptivos)
{
    public static OpcionesCli Parse(string[] args)
    {
        string? rutaFixture = null;
        DateOnly? fecha = null;
        var refiltrar = false;
        string? rutaCriteriosAlternativos = null;
        DateOnly? desde = null;
        string? rutaSalidaRefiltrado = null;
        var backfillUnspsc = false;
        var reevaluarInventario = false;
        string? consultarLicitacion = null;
        var refrescarDescriptivos = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--fixture" when i + 1 < args.Length:
                    rutaFixture = args[++i];
                    break;
                case "--fecha" when i + 1 < args.Length:
                    fecha = DateOnly.ParseExact(args[++i], "dd-MM-yyyy", CultureInfo.InvariantCulture);
                    break;
                case "--refiltrar":
                    refiltrar = true;
                    break;
                case "--criterios" when i + 1 < args.Length:
                    rutaCriteriosAlternativos = args[++i];
                    break;
                case "--desde" when i + 1 < args.Length:
                    desde = DateOnly.ParseExact(args[++i], "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    break;
                case "--salida" when i + 1 < args.Length:
                    rutaSalidaRefiltrado = args[++i];
                    break;
                case "--backfill-unspsc":
                    backfillUnspsc = true;
                    break;
                case "--reevaluar-inventario":
                    reevaluarInventario = true;
                    break;
                case "--consultar-licitacion" when i + 1 < args.Length:
                    consultarLicitacion = args[++i];
                    break;
                case "--refrescar-descriptivos":
                    refrescarDescriptivos = true;
                    break;
            }
        }

        return new OpcionesCli(
            rutaFixture, fecha, refiltrar, rutaCriteriosAlternativos, desde, rutaSalidaRefiltrado,
            backfillUnspsc, reevaluarInventario, consultarLicitacion, refrescarDescriptivos);
    }
}
