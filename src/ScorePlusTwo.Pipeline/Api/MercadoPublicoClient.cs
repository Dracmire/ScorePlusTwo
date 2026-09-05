using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScorePlusTwo.Pipeline.Modelos;
using ScorePlusTwo.Pipeline.Persistencia;

namespace ScorePlusTwo.Pipeline.Api;

// Acceso a la API de ChileCompra, aislado en un solo módulo (SPEC §2: la API
// está en beta y ChileCompra licita su reemplazo).
public sealed class MercadoPublicoClient
{
    private const string BaseUrl = "https://api.mercadopublico.cl/servicios/v1/publico/licitaciones.json";

    // Backoff exponencial: 2s, 4s, 8s, 16s, 32s (5 reintentos tras el intento inicial).
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(32),
    };

    private const int VolumenActivasSospechoso = 5000;

    private readonly HttpClient _http;
    private readonly string _ticket;

    public MercadoPublicoClient(HttpClient http, string ticket)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(30);
        _ticket = ticket;
    }

    public Task<ListadoLicitacionesResponse> ObtenerListadoDiarioAsync(DateOnly fecha, CancellationToken ct = default) =>
        ObtenerAsync($"{BaseUrl}?fecha={FormatearFecha(fecha)}&ticket={_ticket}", ct);

    public Task<ListadoLicitacionesResponse> ObtenerAdjudicadasAsync(DateOnly fecha, CancellationToken ct = default) =>
        ObtenerAsync($"{BaseUrl}?fecha={FormatearFecha(fecha)}&estado=adjudicada&ticket={_ticket}", ct);

    private static string FormatearFecha(DateOnly fecha) => fecha.ToString("ddMMyyyy", CultureInfo.InvariantCulture);

    // Barrido semanal: corte transversal del mercado, sin parámetro de fecha.
    // A diferencia de los otros dos métodos, el llamador (Program.cs) trata
    // tanto el fallo de red como el chequeo de integridad de este método como
    // NO fatales — ver Orquestación en el plan: `activas` es recuperable
    // corriéndola de nuevo cualquier día, el lote diario no.
    public async Task<ListadoLicitacionesResponse> ObtenerActivasAsync(CancellationToken ct = default)
    {
        var respuesta = await ObtenerAsync($"{BaseUrl}?estado=activas&ticket={_ticket}", ct);

        if (respuesta.Cantidad != respuesta.Listado.Count)
        {
            throw new MercadoPublicoApiException(
                $"Respuesta de 'activas' inconsistente: Cantidad={respuesta.Cantidad} pero " +
                $"Listado.Count={respuesta.Listado.Count} (posible paginación no documentada).");
        }

        if (respuesta.Listado.Count > VolumenActivasSospechoso)
        {
            Console.Error.WriteLine(
                $"[ADVERTENCIA] El barrido 'activas' trajo {respuesta.Listado.Count} registros — " +
                "volumen inusualmente alto, revisar manualmente si la API realmente entrega el listado " +
                "completo sin paginar.");
        }

        return respuesta;
    }

    private async Task<ListadoLicitacionesResponse> ObtenerAsync(string url, CancellationToken ct)
    {
        Exception? ultimaExcepcion = null;

        for (var intento = 0; intento <= Backoff.Length; intento++)
        {
            try
            {
                using var respuesta = await _http.GetAsync(url, ct);
                if (respuesta.IsSuccessStatusCode)
                {
                    var json = await respuesta.Content.ReadAsStringAsync(ct);
                    return JsonSerializer.Deserialize<ListadoLicitacionesResponse>(json, JsonOpciones.ApiLectura)
                        ?? throw new MercadoPublicoApiException(
                            $"La API devolvió un cuerpo vacío o no parseable para {OcultarTicket(url)}.");
                }

                if (!EsReintentable(respuesta.StatusCode))
                {
                    throw new MercadoPublicoApiException(
                        $"La API respondió {(int)respuesta.StatusCode} {respuesta.StatusCode} de forma no " +
                        $"recuperable para {OcultarTicket(url)}.");
                }

                ultimaExcepcion = new MercadoPublicoApiException(
                    $"La API respondió {(int)respuesta.StatusCode} {respuesta.StatusCode}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                ultimaExcepcion = ex;
            }

            if (intento < Backoff.Length)
            {
                await Task.Delay(Backoff[intento], ct);
            }
        }

        throw new MercadoPublicoApiException(
            $"Agotados los reintentos contra {OcultarTicket(url)}.", ultimaExcepcion);
    }

    private static bool EsReintentable(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static string OcultarTicket(string url) =>
        Regex.Replace(url, "ticket=[^&]+", "ticket=***", RegexOptions.None, TimeSpan.FromSeconds(1));
}
