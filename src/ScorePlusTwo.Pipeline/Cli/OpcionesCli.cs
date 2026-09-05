using System.Globalization;

namespace ScorePlusTwo.Pipeline.Cli;

// Parseo manual mínimo, sin dependencia de System.CommandLine: solo dos flags.
public sealed record OpcionesCli(string? RutaFixture, DateOnly? Fecha)
{
    public static OpcionesCli Parse(string[] args)
    {
        string? rutaFixture = null;
        DateOnly? fecha = null;

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
            }
        }

        return new OpcionesCli(rutaFixture, fecha);
    }
}
