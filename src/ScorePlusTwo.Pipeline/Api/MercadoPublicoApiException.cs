namespace ScorePlusTwo.Pipeline.Api;

public sealed class MercadoPublicoApiException : Exception
{
    public MercadoPublicoApiException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
