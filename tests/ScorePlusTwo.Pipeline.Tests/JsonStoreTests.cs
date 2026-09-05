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
}
