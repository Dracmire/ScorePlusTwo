using System.Text.Json;
using ScorePlusTwo.Pipeline.Modelos;
using ScorePlusTwo.Pipeline.Persistencia;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

// FechaCierre viene de la API oficial en hora local de Chile, sin sufijo de
// zona. Este test protege contra que un cambio futuro en JsonOpciones (ej.
// agregar un JsonConverter<DateTime> global) empiece a interpretarla como
// UTC y le agregue "Z", o a convertirla — ver la nota en LicitacionRaw.cs
// sobre la discrepancia de 7 días detectada con LicitaLab para 4956-74-LE26.
public class FechaCierreTests
{
    [Fact]
    public void Deserializar_NoConvierteNiAgregaSufijoDeZona()
    {
        const string json = """{"CodigoExterno":"4956-74-LE26","Nombre":"X","CodigoEstado":5,"FechaCierre":"2026-09-14T15:01:00"}""";

        var licitacion = JsonSerializer.Deserialize<LicitacionRaw>(json, JsonOpciones.ApiLectura)!;

        Assert.Equal(DateTimeKind.Unspecified, licitacion.FechaCierre!.Value.Kind);
        Assert.Equal(new DateTime(2026, 9, 14, 15, 1, 0), licitacion.FechaCierre);
    }

    [Fact]
    public void Serializar_NoAgregaSufijoDeZona()
    {
        var licitacion = new LicitacionRaw("4956-74-LE26", "X", 5, new DateTime(2026, 9, 14, 15, 1, 0));

        var json = JsonSerializer.Serialize(licitacion, JsonOpciones.ApiLectura);

        Assert.Contains("\"2026-09-14T15:01:00\"", json);
        Assert.DoesNotContain("Z\"", json);
        Assert.DoesNotContain("+", json);
    }
}
