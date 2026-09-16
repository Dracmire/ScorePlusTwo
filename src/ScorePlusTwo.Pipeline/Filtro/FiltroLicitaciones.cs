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
        // Tramo bajo: no pasa por enriquecimiento ni clasificación de rubro,
        // va directo a su lista.
        var tramoBajo = sobrevivientes.TramoBajo
            .Select(item => new CandidataDetectada(item.Licitacion, item.Tipo, RubroMatch: null, TerminoMatch: null))
            .ToList();

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

        // 4. UNSPSC decide bien-vs-servicio; rubro (compliance/ti/ia) solo
        // se evalúa sobre lo que UNSPSC ya confirmó como servicio — deja de
        // ser el filtro de entrada. Bien/SinResolver/PendienteEnriquecimiento
        // nunca se destruyen ni se promueven a Prioritarias sin esa
        // confirmación: van a Secundarias con UnspscEstado visible.
        var bienes = 0;
        var sinResolverUnspsc = 0;

        foreach (var (licitacion, tipo) in sobrevivientes.Regular)
        {
            cacheUnspsc.TryGetValue(licitacion.CodigoExterno, out var entrada);
            var estadoUnspsc = ClasificadorUnspsc.Clasificar(entrada, catalogoUnspsc);
            var codigosProducto = entrada?.Items
                .Where(i => i.CodigoProducto is not null)
                .Select(i => i.CodigoProducto!.Value)
                .ToList() ?? new List<int>();

            if (estadoUnspsc != UnspscEstado.Servicio)
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
                    licitacion, tipo, RubroMatch: null, TerminoMatch: null, estadoUnspsc, codigosProducto));
                continue;
            }

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
                // Servicio confirmado, pero sin rubro conocido: inventario
                // crudo de prospección.
                secundarias.Add(new CandidataDetectada(
                    licitacion, tipo, RubroMatch: null, TerminoMatch: null, estadoUnspsc, codigosProducto));
                continue;
            }

            var candidata = new CandidataDetectada(
                licitacion, tipo, rubroEncontrado.Id, terminoMatch, estadoUnspsc, codigosProducto);

            if (rubroEncontrado.Prioridad == "alta")
            {
                prioritarias.Add(candidata);
            }
            else
            {
                secundarias.Add(candidata);
            }
        }

        // 5. Región — no-op en F1 (todas las prioritarias pasan). Etapa
        // identidad a propósito, para que F2 la reemplace sin reestructurar
        // el resto del pipeline. No se aplica sobre Secundarias ni TramoBajo.
        var trasRegion = prioritarias.Count;

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
            SinResolverUnspsc: sinResolverUnspsc);
    }
}
