namespace ScorePlusTwo.Pipeline.Infraestructura;

// Resuelve la raíz del repo subiendo desde AppContext.BaseDirectory hasta
// encontrar ScorePlusTwo.sln. Así, tanto el ejecutable real como los tests
// (vía ProjectReference) resuelven rutas relativas a config/, data/,
// tests/fixtures/ de forma robusta, sin depender del directorio de trabajo
// desde el que se invoque `dotnet run`/`dotnet test`.
public static class RutaRepo
{
    public static string Resolver()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null)
        {
            if (File.Exists(Path.Combine(directorio.FullName, "ScorePlusTwo.sln")))
            {
                return directorio.FullName;
            }

            directorio = directorio.Parent;
        }

        throw new InvalidOperationException(
            $"No se pudo encontrar ScorePlusTwo.sln subiendo desde {AppContext.BaseDirectory}");
    }
}
