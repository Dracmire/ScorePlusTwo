using ScorePlusTwo.Pipeline.Modelos;

namespace ScorePlusTwo.Pipeline.Filtro;

// Función pura: mismo input siempre produce el mismo output, sin I/O.
//
// Diseño (reemplaza el filtro de una sola lista + descarte silencioso):
// hoy solo obras públicas y suministros (descarte_duro) deben desaparecer
// sin rastro — no hay negocio posible ahí. Todo lo demás se CLASIFICA en
// una de tres listas, nunca se destruye:
//   - Prioritarias (Lista A): rubro de prioridad "alta", sin bloqueo de
//     exclusiones_rubro. Es lo que hoy va al tablero.
//   - Secundarias (Lista B): sobrevive estado+tipo+descarte_duro pero no
//     entra a Prioritarias (rubro secundario, sin rubro, bloqueada por
//     exclusiones_rubro, o de un tipo privado). Inventario para prospectar
//     rubros no atendidos.
//   - TramoBajo: tipo L1, aceptado pero fuera de la clasificación de rubro
//     — no se mezcla con el resto, es opción solo si aparece un cliente.
// Orden: estado -> tipo -> descarte_duro -> (L1: tramo bajo | tipo privado:
// secundarias sin pasar por rubro | resto: rubro).
public static class FiltroLicitaciones
{
    // Excepciones puntuales al descarte duro, documentadas caso a caso: un
    // término de descarte_duro coincide por una mención incidental, no
    // porque el registro sea efectivamente una compra de obra/bien. Vive en
    // código (no en config/criterios.json) porque no es un criterio de
    // negocio editable — es la corrección de un falso positivo específico
    // encontrado al ampliar descarte_duro, y solo tiene sentido documentado
    // junto al análisis que lo originó.
    //
    // DEUDA TÉCNICA: este mecanismo escala mal. Si llega a 5 entradas, dejó
    // de ser viable mantener excepciones por código uno por uno — la señal
    // sería que descarte_duro necesita una forma real de "rescatar" a
    // Secundarias en vez de destruir sin rastro (hoy no la tiene: es un veto
    // duro por diseño, ver comentario de clase). Rediseñar en ese punto,
    // no seguir agregando entradas.
    private static readonly HashSet<string> ExcepcionesDescarteDuro = new()
    {
        // "Convenio Suministro de Servicio de Fotocopiado Impresión y
        // Digitalización con Entrega de Equipos" — matchea "equipos" por la
        // frase "Entrega de Equipos", pero el objeto del contrato es un
        // SERVICIO de fotocopiado/impresión/digitalización, no una compra de
        // equipamiento. Sin esta excepción, "equipos" en descarte_duro lo
        // destruiría sin rastro en vez de dejarlo en Prioritarias (matchea
        // "informátic" del rubro ti). Verificado en la simulación de
        // descarte_duro ampliado (2026-09-09): de 54 prioritarias vigentes,
        // fue el único de 8 casos perdidos que no era una compra real de
        // bien — los otros 7 (ej. "COMPRA DE EQUIPOS INFORMATICOS") sí lo son.
        "1057548-21-LE26",
    };

    public static ResultadoFiltro Filtrar(IEnumerable<LicitacionRaw> licitaciones, Criterios criterios)
    {
        var lista = licitaciones as IReadOnlyList<LicitacionRaw> ?? licitaciones.ToList();
        var total = lista.Count;

        // 1. Estado
        var trasEstado = lista.Where(l => criterios.Estados.Contains(l.CodigoEstado)).ToList();

        // 2. Tipo (derivado de CodigoExterno; un código malformado simplemente no matchea).
        // L1 y los tipos privados (CO/B2/E2/H2/I2) se separan aquí: ninguno pasa
        // por clasificación de rubro.
        var tramoBajoCandidatos = new List<(LicitacionRaw Licitacion, string Tipo)>();
        var tipoPrivadoCandidatos = new List<(LicitacionRaw Licitacion, string Tipo)>();
        var tipoRegularCandidatos = new List<(LicitacionRaw Licitacion, string Tipo)>();
        foreach (var licitacion in trasEstado)
        {
            if (!CodigoExternoParser.TryExtraerTipoAnio(licitacion.CodigoExterno, out var tipo, out _)
                || !criterios.Tipos.Contains(tipo))
            {
                continue;
            }

            if (tipo == "L1")
            {
                tramoBajoCandidatos.Add((licitacion, tipo));
            }
            else if (criterios.TiposPrivados.Contains(tipo))
            {
                tipoPrivadoCandidatos.Add((licitacion, tipo));
            }
            else
            {
                tipoRegularCandidatos.Add((licitacion, tipo));
            }
        }

        var trasTipo = tramoBajoCandidatos.Count + tipoPrivadoCandidatos.Count + tipoRegularCandidatos.Count;

        // 3. Descarte duro — obras públicas y suministros, gana siempre y se
        // evalúa antes de cualquier rubro. Aplica por igual a tipos normales,
        // L1 y tipos privados: es una regla de negocio (no hay cliente
        // posible), no algo específico de la clasificación por rubro.
        var descarteDuroNormalizado = criterios.DescarteDuro
            .Select(TextoNormalizador.Normalizar)
            .ToList();

        bool EsDescarteDuro(LicitacionRaw l)
        {
            if (ExcepcionesDescarteDuro.Contains(l.CodigoExterno))
            {
                return false;
            }

            var nombreNormalizado = TextoNormalizador.Normalizar(l.Nombre);
            return descarteDuroNormalizado.Any(termino => nombreNormalizado.Contains(termino, StringComparison.Ordinal));
        }

        var tramoBajoSobreviviente = tramoBajoCandidatos.Where(item => !EsDescarteDuro(item.Licitacion)).ToList();
        var tipoPrivadoSobreviviente = tipoPrivadoCandidatos.Where(item => !EsDescarteDuro(item.Licitacion)).ToList();
        var regularSobreviviente = tipoRegularCandidatos.Where(item => !EsDescarteDuro(item.Licitacion)).ToList();

        var descarteDuroCount = (tramoBajoCandidatos.Count - tramoBajoSobreviviente.Count)
            + (tipoPrivadoCandidatos.Count - tipoPrivadoSobreviviente.Count)
            + (tipoRegularCandidatos.Count - regularSobreviviente.Count);

        // Tramo bajo: no pasa por clasificación de rubro, va directo a su lista.
        var tramoBajo = tramoBajoSobreviviente
            .Select(item => new CandidataDetectada(item.Licitacion, item.Tipo, RubroMatch: null, TerminoMatch: null))
            .ToList();

        // 4. Rubro — sobre TODOS los rubros, en el orden del archivo. El
        // primer match decide. exclusiones_rubro (software/servidor) ya no
        // mata el registro: solo le impide entrar a Prioritarias cuando el
        // rubro que matcheó es de prioridad alta, y lo manda a Secundarias
        // en su lugar, conservando el rubro_match/termino_match encontrado.
        var exclusionesRubroNormalizadas = criterios.ExclusionesRubro
            .Select(TextoNormalizador.Normalizar)
            .ToList();

        var prioritarias = new List<CandidataDetectada>();
        var secundarias = new List<CandidataDetectada>();

        // Tipos privados (CO/B2/E2/H2/I2): van siempre a Secundarias, sin
        // pasar por clasificación de rubro — tienen ciclo de vida real
        // (ventana de postulación) pero algunos cierran el mismo día en que
        // aparecen, así que no compiten por Prioritarias todavía (revisión
        // pendiente para promoverlos). Candidata.TipoPrivado (derivado de
        // Tipo en Program.cs) es lo que permite encontrarlos en la lista.
        secundarias.AddRange(tipoPrivadoSobreviviente
            .Select(item => new CandidataDetectada(item.Licitacion, item.Tipo, RubroMatch: null, TerminoMatch: null)));

        foreach (var (licitacion, tipo) in regularSobreviviente)
        {
            var nombreNormalizado = TextoNormalizador.Normalizar(licitacion.Nombre);

            RubroCriterio? rubroEncontrado = null;
            string? terminoMatch = null;
            foreach (var rubro in criterios.Rubros)
            {
                var match = rubro.Terminos.FirstOrDefault(
                    termino => nombreNormalizado.Contains(TextoNormalizador.Normalizar(termino), StringComparison.Ordinal));
                if (match is not null)
                {
                    rubroEncontrado = rubro;
                    terminoMatch = match;
                    break;
                }
            }

            if (rubroEncontrado is null)
            {
                // Sin rubro: inventario crudo de prospección.
                secundarias.Add(new CandidataDetectada(licitacion, tipo, RubroMatch: null, TerminoMatch: null));
                continue;
            }

            var candidata = new CandidataDetectada(licitacion, tipo, rubroEncontrado.Id, terminoMatch);

            if (rubroEncontrado.Prioridad != "alta")
            {
                secundarias.Add(candidata);
                continue;
            }

            var bloqueadaPorExclusionRubro = exclusionesRubroNormalizadas
                .Any(termino => nombreNormalizado.Contains(termino, StringComparison.Ordinal));

            if (bloqueadaPorExclusionRubro)
            {
                secundarias.Add(candidata);
            }
            else
            {
                prioritarias.Add(candidata);
            }
        }

        // 5. Región — no-op en F1 (todas las prioritarias pasan). Etapa
        // identidad a propósito, para que F2 la reemplace sin reestructurar
        // el resto del pipeline. No se aplica sobre Secundarias ni TramoBajo.
        var trasRegion = prioritarias.Count;

        return new ResultadoFiltro(
            Total: total,
            TrasEstado: trasEstado.Count,
            TrasTipo: trasTipo,
            DescarteDuro: descarteDuroCount,
            TrasRegion: trasRegion,
            Prioritarias: prioritarias,
            Secundarias: secundarias,
            TramoBajo: tramoBajo);
    }
}
