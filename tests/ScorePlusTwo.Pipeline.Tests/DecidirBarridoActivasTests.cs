using ScorePlusTwo.Pipeline;

namespace ScorePlusTwo.Pipeline.Tests;

// EvaluarUmbralBarridoActivas (2026-10-01) reemplaza "hoy es lunes en
// Chile" por un umbral objetivo de días transcurridos desde el
// activas-*.json más reciente -- el chequeo por día de la semana sufría la
// misma variación de horario del cron (1h52-4h58 de atraso ya documentado)
// y podía saltarse un lunes completo, o -- con el recupero de fechas
// faltantes del mismo follow-up -- correr dos veces en la misma semana si
// el loop cruza un lunes de por medio.
public class DecidirBarridoActivasTests
{
    [Fact]
    public void EvaluarUmbralBarridoActivas_SinActivasPrevia_Corresponde()
    {
        var (corresponde, _) = Program.EvaluarUmbralBarridoActivas(
            fechaMasReciente: null, hoy: new DateOnly(2026, 10, 1), umbralDias: 7);

        Assert.True(corresponde);
    }

    [Fact]
    public void EvaluarUmbralBarridoActivas_ExactamenteSieteDias_Corresponde()
    {
        var hoy = new DateOnly(2026, 10, 8);
        var fechaMasReciente = new DateOnly(2026, 10, 1);

        var (corresponde, _) = Program.EvaluarUmbralBarridoActivas(fechaMasReciente, hoy, umbralDias: 7);

        Assert.True(corresponde);
    }

    [Fact]
    public void EvaluarUmbralBarridoActivas_SeisDias_NoCorresponde()
    {
        var hoy = new DateOnly(2026, 10, 7);
        var fechaMasReciente = new DateOnly(2026, 10, 1);

        var (corresponde, _) = Program.EvaluarUmbralBarridoActivas(fechaMasReciente, hoy, umbralDias: 7);

        Assert.False(corresponde);
    }

    // Relevante con el recupero de fechas faltantes corriendo dos veces el
    // mismo día real (ej. una corrida manual y el cron automático).
    [Fact]
    public void EvaluarUmbralBarridoActivas_CeroDias_NoCorresponde()
    {
        var hoy = new DateOnly(2026, 10, 1);

        var (corresponde, _) = Program.EvaluarUmbralBarridoActivas(fechaMasReciente: hoy, hoy, umbralDias: 7);

        Assert.False(corresponde);
    }
}
