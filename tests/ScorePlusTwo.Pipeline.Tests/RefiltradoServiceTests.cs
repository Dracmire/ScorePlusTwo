using ScorePlusTwo.Pipeline.Refiltrado;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

public class RefiltradoServiceTests
{
    [Theory]
    [InlineData("2026-09-04.json", "2026-09-04")]
    [InlineData("activas-2026-09-05.json", "2026-09-05")]
    public void ExtraerFechaDeArchivo_ParseaNombresValidos(string nombreArchivo, string fechaEsperada)
    {
        var fecha = RefiltradoService.ExtraerFechaDeArchivo(nombreArchivo);

        Assert.Equal(DateOnly.Parse(fechaEsperada), fecha);
    }

    [Theory]
    [InlineData(".gitkeep")]
    [InlineData("criterios.json")]
    [InlineData("2026-9-4.json")]
    public void ExtraerFechaDeArchivo_RetornaNullParaNombresQueNoMatchean(string nombreArchivo)
    {
        Assert.Null(RefiltradoService.ExtraerFechaDeArchivo(nombreArchivo));
    }

    [Fact]
    public void GenerarCsv_EncomillaCamposConComaOComillas()
    {
        var filas = new[]
        {
            new FilaRefiltrado(
                Codigo: "1-1-LE26",
                Nombre: "SERVICIO, CON COMA Y \"COMILLAS\"",
                Tipo: "LE",
                RubroMatch: "compliance",
                TerminoMatch: "auditor",
                FechaCierre: new DateTime(2026, 9, 10, 15, 0, 0),
                ArchivoOrigen: "2026-09-05.json",
                FechaLote: new DateOnly(2026, 9, 5)),
        };

        var csv = RefiltradoService.GenerarCsv(filas);

        Assert.Contains("\"SERVICIO, CON COMA Y \"\"COMILLAS\"\"\"", csv);
        Assert.Contains("2026-09-05.json", csv);
        Assert.Contains("2026-09-05", csv);
        Assert.StartsWith("codigo,nombre,tipo,rubro_match,termino_match,fecha_cierre,archivo_origen,fecha_lote\r\n", csv);
    }

    [Fact]
    public void GenerarCsv_SinCandidatas_SoloDejaElEncabezado()
    {
        var csv = RefiltradoService.GenerarCsv(Array.Empty<FilaRefiltrado>());

        Assert.Equal("codigo,nombre,tipo,rubro_match,termino_match,fecha_cierre,archivo_origen,fecha_lote\r\n", csv);
    }
}
