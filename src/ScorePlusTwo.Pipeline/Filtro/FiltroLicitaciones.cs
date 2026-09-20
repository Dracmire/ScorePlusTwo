using ScorePlusTwo.Pipeline.Modelos;
using ScorePlusTwo.Pipeline.Unspsc;

namespace ScorePlusTwo.Pipeline.Filtro;

// Funciones puras: mismo input siempre produce el mismo output, sin I/O.
//
// Diseño (reemplaza el filtro de una sola lista + descarte silencioso):
// hoy solo obras públicas y suministros (descarte_duro) deben desaparecer
// sin rastro — no hay negocio posible ahí. Todo lo demás se CLASIFICA en
// una de tres listas, nunca se destruye:
//   - Prioritarias (Lista A): rubro de prioridad "alta". Es lo que hoy va
//     al tablero.
//   - Secundarias (Lista B): sobrevive estado+tipo+descarte_duro pero no
//     entra a Prioritarias (rubro secundario, sin rubro, tipo privado, o
//     UNSPSC no confirmó servicio). Inventario para prospectar rubros no
//     atendidos.
//   - TramoBajo: tipo L1, aceptado pero fuera de la clasificación de rubro
//     — no se mezcla con el resto, es opción solo si aparece un cliente.
// Orden: estado -> tipo -> descarte_duro -> (L1: tramo bajo | tipo privado:
// secundarias sin pasar por rubro | resto: enriquecimiento UNSPSC -> rubro
// solo si es servicio).
//
// Dividido en dos pasos (F2, 2026-09-16) porque el enriquecimiento UNSPSC
// es impuro (llama a la API): FiltrarHastaDescarteDuro es puro y expone los
// sobrevivientes "Regular" para que el llamador (Program.cs) sepa qué
// CodigoExterno enriquecer antes de clasificar. ClasificarYFiltrarRubro
// recibe el cache/catálogo ya cargados como argumentos — sigue sin hacer
// I/O, igual que Criterios se carga afuera y se pasa como argumento.
public static class FiltroLicitaciones
{
    public static SobrevivientesDescarteDuro FiltrarHastaDescarteDuro(
        IEnumerable<LicitacionRaw> licitaciones, Criterios criterios)
    {
        var lista = licitaciones as IReadOnlyList<LicitacionRaw> ?? licitaciones.ToList();
        var total = lista.Count;

        // 1. Estado
        var trasEstado = lista.Where(l => criterios.Estados.Contains(l.CodigoEstado)).ToList();

        // 2. Tipo (derivado de CodigoExterno; un código malformado simplemente no matchea).
        // L1 y los tipos privados (CO/B2/E2/H2/I2) se separan aquí: ninguno pasa
        // por enriquecimiento ni clasificación de rubro.
        var tramoBajoCandidatos = new List<CandidataParcial>();
        var tipoPrivadoCandidatos = new List<CandidataParcial>();
        var tipoRegularCandidatos = new List<CandidataParcial>();
        foreach (var licitacion in trasEstado)
        {
            if (!CodigoExternoParser.TryExtraerTipoAnio(licitacion.CodigoExterno, out var tipo, out _)
                || !criterios.Tipos.Contains(tipo))
            {
                continue;
            }

            if (tipo == "L1")
            {
                tramoBajoCandidatos.Add(new CandidataParcial(licitacion, tipo));
            }
            else if (criterios.TiposPrivados.Contains(tipo))
            {
                tipoPrivadoCandidatos.Add(new CandidataParcial(licitacion, tipo));
            }
            else
            {
                tipoRegularCandidatos.Add(new CandidataParcial(licitacion, tipo));
            }
        }

        var trasTipo = tramoBajoCandidatos.Count + tipoPrivadoCandidatos.Count + tipoRegularCandidatos.Count;

        // 3. Descarte duro — obras públicas y suministros, gana siempre y se
        // evalúa antes de cualquier rubro. Aplica por igual a tipos normales,
        // L1 y tipos privados: es una regla de negocio (no hay cliente
        // posible), no algo específico de la clasificación por rubro. Núcleo
        // mínimo desde F2: solo ahorra llamadas de enriquecimiento sobre lo
        // evidentemente fuera de negocio, ya no decide bien-vs-servicio.
        var descarteDuroNormalizado = criterios.DescarteDuro
            .Select(TextoNormalizador.Normalizar)
            .ToList();

        bool EsDescarteDuro(LicitacionRaw l)
        {
            var nombreNormalizado = TextoNormalizador.Normalizar(l.Nombre);
            return descarteDuroNormalizado.Any(termino => nombreNormalizado.Contains(termino, StringComparison.Ordinal));
        }

        var tramoBajoSobreviviente = tramoBajoCandidatos.Where(item => !EsDescarteDuro(item.Licitacion)).ToList();
        var tipoPrivadoSobreviviente = tipoPrivadoCandidatos.Where(item => !EsDescarteDuro(item.Licitacion)).ToList();
        var regularSobreviviente = tipoRegularCandidatos.Where(item => !EsDescarteDuro(item.Licitacion)).ToList();

        var descarteDuroCount = (tramoBajoCandidatos.Count - tramoBajoSobreviviente.Count)
            + (tipoPrivadoCandidatos.Count - tipoPrivadoSobreviviente.Count)
            + (tipoRegularCandidatos.Count - regularSobreviviente.Count);

        return new SobrevivientesDescarteDuro(
            Total: total,
            TrasEstado: trasEstado.Count,
            TrasTipo: trasTipo,
            DescarteDuro: descarteDuroCount,
            TramoBajo: tramoBajoSobreviviente,
            TipoPrivado: tipoPrivadoSobreviviente,
            Regular: regularSobreviviente);
    }

    // cacheUnspsc: todo lo que ya se sabe de enriquecimiento, indexado por
    // CodigoExterno (ver EntradaCacheUnspsc) — un código ausente del
    // diccionario se clasifica como UnspscEstado.PendienteEnriquecimiento,
    // nunca lanza. catalogoUnspsc resuelve la raíz de cada CodigoProducto
    // cacheado (ver CatalogoUnspsc).
    public static ResultadoFiltro ClasificarYFiltrarRubro(
        SobrevivientesDescarteDuro sobrevivientes,
        Criterios criterios,
        IReadOnlyDictionary<string, EntradaCacheUnspsc> cacheUnspsc,
        CatalogoUnspsc catalogoUnspsc)
    {
        var prioritarias = new List<CandidataDetectada>();
        var secundarias = new List<CandidataDetectada>();

        // Tipos privados (CO/B2/E2/H2/I2): van siempre a Secundarias, sin
        // pasar por enriquecimiento ni clasificación de rubro — tienen ciclo
        // de vida real (ventana de postulación) pero algunos cierran el
        // mismo día en que aparecen, así que no compiten por Prioritarias
        // todavía (revisión pendiente para promoverlos). Candidata.
        // TipoPrivado (derivado de Tipo en Program.cs) es lo que permite
        // encontrarlos en la lista.
        secundarias.AddRange(sobrevivientes.TipoPrivado
            .Select(item => new CandidataDetectada(item.Licitacion, item.Tipo, RubroMatch: null, TerminoMatch: null)));

        // 4. UNSPSC decide bien-vs-servicio; rubro (compliance/ti/ia) se
        // evalúa sobre Servicio (siempre) y RevisionManual (para dar
        // contexto al humano que revisa, aunque nunca llegue a
        // Prioritarias). Bien/SinResolver/PendienteEnriquecimiento nunca
        // se destruyen ni se promueven a Prioritarias sin que UNSPSC haya
        // confirmado servicio: van a Secundarias con UnspscEstado visible.
        var bienes = 0;
        var sinResolverUnspsc = 0;
        var revisionManualUnspsc = 0;
        var trasRegion = 0;
        var familiasRevisionManual = criterios.FamiliasUnspscRevisionManual.ToHashSet();

        // Términos ambiguos (RubroCriterio.TerminosAmbiguos, 2026-09-19): un
        // match SOLO contra un término marcado como ambiguo (ej.
        // "plataforma" en ti — colisiona con suscripciones de puro bien) no
        // es señal suficiente por sí sola. Si el mismo rubro matchea ADEMÁS
        // un término no ambiguo, ese gana de inmediato y el resultado es
        // idéntico al comportamiento anterior a este campo — la ambigüedad
        // nunca descarta nada, solo baja la confianza de una señal única.
        (RubroCriterio? Rubro, string? Termino, bool EsAmbiguo) EvaluarRubro(string nombreNormalizado)
        {
            foreach (var rubro in criterios.Rubros)
            {
                var ambiguos = rubro.TerminosAmbiguos ?? new List<string>();
                string? matchAmbiguo = null;

                foreach (var termino in rubro.Terminos)
                {
                    if (!nombreNormalizado.Contains(TextoNormalizador.Normalizar(termino), StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!ambiguos.Contains(termino, StringComparer.Ordinal))
                    {
                        return (rubro, termino, false);
                    }

                    matchAmbiguo ??= termino;
                }

                if (matchAmbiguo is not null)
                {
                    return (rubro, matchAmbiguo, true);
                }
            }

            return (null, null, false);
        }

        // Tramo bajo: no pasa por enriquecimiento UNSPSC ni por su
        // segmentación de lista (siempre TramoBajo, nunca se auto-promueve
        // a Prioritarias) — pero sí se evalúa rubro por palabra (2026-09-19)
        // igual que Regular, puramente informativo: sirve para prospectar
        // qué hay ahí sin gastar una llamada de detalle sobre potencialmente
        // cientos de L1 diarios.
        var tramoBajo = sobrevivientes.TramoBajo
            .Select(item =>
            {
                var (rubro, termino, _) = EvaluarRubro(TextoNormalizador.Normalizar(item.Licitacion.Nombre));
                return new CandidataDetectada(item.Licitacion, item.Tipo, rubro?.Id, termino);
            })
            .ToList();

        foreach (var (licitacion, tipo) in sobrevivientes.Regular)
        {
            cacheUnspsc.TryGetValue(licitacion.CodigoExterno, out var entrada);
            var estadoUnspsc = ClasificadorUnspsc.Clasificar(entrada, catalogoUnspsc, familiasRevisionManual);
            var codigosProducto = entrada?.Items
                .Where(i => i.CodigoProducto is not null)
                .Select(i => i.CodigoProducto!.Value)
                .ToList() ?? new List<int>();
            var region = entrada?.RegionUnidad;
            var moneda = entrada?.Moneda;
            var monto = entrada?.Monto;
            var cantidadReclamos = entrada?.CantidadReclamos;

            if (estadoUnspsc is UnspscEstado.Bien or UnspscEstado.SinResolver or UnspscEstado.PendienteEnriquecimiento)
            {
                if (estadoUnspsc == UnspscEstado.Bien)
                {
                    bienes++;
                }
                else
                {
                    sinResolverUnspsc++;
                }

                secundarias.Add(new CandidataDetectada(
                    licitacion, tipo, RubroMatch: null, TerminoMatch: null, estadoUnspsc, codigosProducto, region,
                    moneda, monto, cantidadReclamos));
                continue;
            }

            if (estadoUnspsc == UnspscEstado.RevisionManual)
            {
                revisionManualUnspsc++;

                var (rubroRevision, terminoRevision, _) = EvaluarRubro(TextoNormalizador.Normalizar(licitacion.Nombre));
                secundarias.Add(new CandidataDetectada(
                    licitacion, tipo, rubroRevision?.Id, terminoRevision, estadoUnspsc, codigosProducto, region,
                    moneda, monto, cantidadReclamos));
                continue;
            }

            // estadoUnspsc == Servicio: rubro se evalúa SIEMPRE, sin
            // importar la región — el costo es despreciable (matching de
            // strings ya en memoria) y el valor es real: un servicio con
            // rubro alta descartado solo por región queda visible con su
            // RubroMatch poblado en vez de indistinguible de cualquier
            // servicio irrelevante (2026-09-17, corrección explícita del
            // usuario sobre un diseño anterior que cortaba antes de rubro).
            var regionElegible = EsRegionElegible(criterios, region);
            if (regionElegible)
            {
                trasRegion++;
            }

            var (rubroEncontrado, terminoMatch, esAmbiguo) = EvaluarRubro(TextoNormalizador.Normalizar(licitacion.Nombre));
            var calificaParaPrioritarias = rubroEncontrado is not null && rubroEncontrado.Prioridad == "alta" && regionElegible;

            var candidata = new CandidataDetectada(
                licitacion, tipo, rubroEncontrado?.Id, terminoMatch, estadoUnspsc, codigosProducto, region,
                moneda, monto, cantidadReclamos,
                // Habría promovido a Prioritarias de no ser porque el único
                // término que matcheó está marcado como ambiguo — un humano
                // decide desde el tablero (pestaña "Revisión"), lo que
                // escribe un override en data/overrides.json.
                EsRevisionAmbigua: calificaParaPrioritarias && esAmbiguo);

            if (calificaParaPrioritarias && !esAmbiguo)
            {
                prioritarias.Add(candidata);
            }
            else
            {
                secundarias.Add(candidata);
            }
        }

        return new ResultadoFiltro(
            Total: sobrevivientes.Total,
            TrasEstado: sobrevivientes.TrasEstado,
            TrasTipo: sobrevivientes.TrasTipo,
            DescarteDuro: sobrevivientes.DescarteDuro,
            TrasRegion: trasRegion,
            Prioritarias: prioritarias,
            Secundarias: secundarias,
            TramoBajo: tramoBajo,
            Bienes: bienes,
            SinResolverUnspsc: sinResolverUnspsc,
            RevisionManualUnspsc: revisionManualUnspsc);
    }

    // Promovido de función local privada dentro de ClasificarYFiltrarRubro
    // (2026-09-18) para que --backfill-unspsc pueda reusar exactamente la
    // misma regla de elegibilidad sin duplicarla. Una RegionUnidad nula (dato
    // no disponible) nunca es elegible — conservador, nunca promueve a
    // Prioritarias sobre un dato ausente, mismo principio que ya rige
    // SinResolver/PendienteEnriquecimiento.
    public static bool EsRegionElegible(Criterios criterios, string? regionUnidad)
    {
        if (regionUnidad is null)
        {
            return false;
        }

        var regionNormalizada = TextoNormalizador.Normalizar(regionUnidad);
        return criterios.Regiones.Any(r => regionNormalizada.Contains(TextoNormalizador.Normalizar(r), StringComparison.Ordinal));
    }
}
