using ScorePlusTwo.Pipeline.Modelos;

namespace ScorePlusTwo.Pipeline.Filtro;

// Función pura: mismo input siempre produce el mismo output, sin I/O.
// Orden estricto de SPEC §5.2: estado -> tipo -> exclusiones -> rubro -> región.
public static class FiltroLicitaciones
{
    public static ResultadoFiltro Filtrar(IEnumerable<LicitacionRaw> licitaciones, Criterios criterios)
    {
        var lista = licitaciones as IReadOnlyList<LicitacionRaw> ?? licitaciones.ToList();
        var total = lista.Count;

        // 1. Estado
        var trasEstado = lista.Where(l => criterios.Estados.Contains(l.CodigoEstado)).ToList();

        // 2. Tipo (derivado de CodigoExterno; un código malformado simplemente no matchea)
        var trasTipo = new List<(LicitacionRaw Licitacion, string Tipo)>();
        foreach (var licitacion in trasEstado)
        {
            if (CodigoExternoParser.TryExtraerTipoAnio(licitacion.CodigoExterno, out var tipo, out _)
                && criterios.Tipos.Contains(tipo))
            {
                trasTipo.Add((licitacion, tipo));
            }
        }

        // 3. Exclusiones — ganan siempre, se evalúan antes de mirar cualquier rubro
        var exclusionesNormalizadas = criterios.Exclusiones
            .Select(TextoNormalizador.Normalizar)
            .ToList();

        var sobrevivientesExclusion = new List<(LicitacionRaw Licitacion, string Tipo)>();
        foreach (var item in trasTipo)
        {
            var nombreNormalizado = TextoNormalizador.Normalizar(item.Licitacion.Nombre);
            var excluido = exclusionesNormalizadas.Any(termino => nombreNormalizado.Contains(termino, StringComparison.Ordinal));
            if (!excluido)
            {
                sobrevivientesExclusion.Add(item);
            }
        }

        var excluidas = trasTipo.Count - sobrevivientesExclusion.Count;

        // 4. Rubro — sobre TODOS los rubros (activos e inactivos). El primer
        // rubro (según el orden del archivo) que matchea decide: si es
        // activo, candidata; si no, observación. Sin mirar más rubros después
        // del primer match.
        var candidatas = new List<CandidataDetectada>();
        var observaciones = new List<Observacion>();

        foreach (var (licitacion, tipo) in sobrevivientesExclusion)
        {
            var nombreNormalizado = TextoNormalizador.Normalizar(licitacion.Nombre);

            foreach (var rubro in criterios.Rubros)
            {
                var terminoMatch = rubro.Terminos.FirstOrDefault(
                    termino => nombreNormalizado.Contains(TextoNormalizador.Normalizar(termino), StringComparison.Ordinal));

                if (terminoMatch is null)
                {
                    continue;
                }

                if (rubro.Activo)
                {
                    candidatas.Add(new CandidataDetectada(licitacion, tipo, rubro.Id, terminoMatch));
                }
                else
                {
                    observaciones.Add(new Observacion(licitacion.CodigoExterno, licitacion.Nombre, rubro.Id, terminoMatch));
                }

                break;
            }
        }

        // 5. Región — no-op en F1 (todas las candidatas pasan). Etapa
        // identidad a propósito, para que F2 la reemplace sin reestructurar
        // el resto del pipeline. No se aplica sobre Observaciones.
        var trasRegion = candidatas.Count;

        return new ResultadoFiltro(
            Total: total,
            TrasEstado: trasEstado.Count,
            TrasTipo: trasTipo.Count,
            Excluidas: excluidas,
            TrasRubro: candidatas.Count,
            TrasRegion: trasRegion,
            Candidatas: candidatas,
            Observaciones: observaciones);
    }
}
