using ScorePlusTwo.Pipeline.Persistencia;
using Xunit;

namespace ScorePlusTwo.Pipeline.Tests;

public class JsonStoreTests
{
    [Fact]
    public void Cargar_RechazaCsv_ConMensajeClaro()
    {
        var ruta = Path.GetTempFileName();
        try
        {
            File.WriteAllText(ruta, "CodigoExterno;Nombre;Moneda\n85-34-LP26;Algo con ; embebido;CLP\n");

            var excepcion = Assert.Throws<InvalidOperationException>(
                () => JsonStore.Cargar<object>(ruta, JsonOpciones.ApiLectura));

            Assert.Contains("no parsea CSV", excepcion.Message);
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void Cargar_AceptaJsonValido()
    {
        var ruta = Path.GetTempFileName();
        try
        {
            File.WriteAllText(ruta, """{"Cantidad":0,"FechaCreacion":"x","Version":"v1","Listado":[]}""");

            var resultado = JsonStore.Cargar<Modelos.ListadoLicitacionesResponse>(ruta, JsonOpciones.ApiLectura);

            Assert.Equal(0, resultado.Cantidad);
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    // Guardar (2026-10-01): escritura atómica vía .tmp + File.Move, para que
    // un kill a mitad de escritura (OOM, timeout de Actions) nunca deje un
    // JSON truncado en disco -- ver el commit de diario.yml que ahora corre
    // también tras un fallo controlado del recupero de fechas. No simula un
    // crash a mitad de escritura (no es practicable en xUnit) -- solo
    // confirma que el patrón no deja residuos en el camino feliz.
    [Fact]
    public void Guardar_NoDejaArchivoTemporalResidual_YElContenidoFinalEsElGuardado()
    {
        var ruta = Path.GetTempFileName();
        var rutaTemporal = ruta + ".tmp";
        try
        {
            JsonStore.Guardar(ruta, new { valor = 42 }, JsonOpciones.Persistencia);

            Assert.False(File.Exists(rutaTemporal));
            var leido = JsonStore.Cargar<Dictionary<string, int>>(ruta, JsonOpciones.Persistencia);
            Assert.Equal(42, leido["valor"]);
        }
        finally
        {
            File.Delete(ruta);
            File.Delete(rutaTemporal);
        }
    }
}
