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
// --experimento-unspsc es un modo temporal (ver plan de sesión): pide el
// detalle real de una lista fija de códigos, mide latencia y compara la
// categoría UNSPSC resuelta contra la clasificación actual del filtro de
// palabras. Se elimina junto con .github/workflows/experimento-unspsc.yml
// una vez leído el resultado — mismo ciclo de vida que experimento-cron.yml.
public sealed record OpcionesCli(
    string? RutaFixture,
    DateOnly? Fecha,
    bool Refiltrar,
    string? RutaCriteriosAlternativos,
    DateOnly? Desde,
    string? RutaSalidaRefiltrado,
    bool ExperimentoUnspsc)
{
    public static OpcionesCli Parse(string[] args)
    {
        string? rutaFixture = null;
        DateOnly? fecha = null;
        var refiltrar = false;
        string? rutaCriteriosAlternativos = null;
        DateOnly? desde = null;
        string? rutaSalidaRefiltrado = null;
        var experimentoUnspsc = false;

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
                case "--experimento-unspsc":
                    experimentoUnspsc = true;
                    break;
            }
        }

        return new OpcionesCli(
            rutaFixture, fecha, refiltrar, rutaCriteriosAlternativos, desde, rutaSalidaRefiltrado, experimentoUnspsc);
    }
}
