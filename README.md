# ScorePlusTwo
A Business intelligence monitoring and filtering for B2G

## Pipeline diario

Corre automáticamente todos los días vía GitHub Actions
(`.github/workflows/diario.yml`): descarga el lote diario de licitaciones de
Mercado Público, lo filtra contra `config/criterios.json` y actualiza
`data/informes.json`, `data/eventos.json` y el tablero en `docs/`.

El filtro clasifica cada registro en una de tres listas — nunca en una sola,
y solo obras públicas/suministros (`descarte_duro`) desaparecen sin rastro:

- **Prioritarias** (`data/candidatas.json`): UNSPSC confirma que es un
  servicio real (no un bien ni una familia UNSPSC ambigua en modalidad),
  el comprador está en una región de `config/criterios.json`, y matchea un
  rubro de `prioridad: "alta"`. Es lo que va al tablero.
- **Secundarias** (`data/secundarias.json`): sobreviven estado+tipo+
  descarte_duro pero no entran a Prioritarias — bien confirmado, familia
  UNSPSC ambigua en modalidad, servicio real pero fuera de cobertura
  geográfica, sin enriquecer todavía, rubro de prioridad `"secundaria"`,
  sin ningún rubro, o tipo de licitación privada. Es el inventario para
  prospectar rubros que todavía no se atienden.
- **Tramo bajo** (`data/tramo_bajo.json`): tipo `L1`, aceptado pero nunca se
  mezcla con el resto — es opción solo si aparece un cliente que la tome, sin
  importar qué rubro matchee. Sí se evalúa contra `rubros` (con términos
  ambiguos incluidos) para dejar `rubro_match`/`termino_match` visibles en el
  tablero/CSV como dato de prospección, pero eso nunca cambia su
  segmentación ni gasta una llamada de enriquecimiento UNSPSC.

Para correrlo manualmente sobre un fixture local, sin red ni `MP_TICKET`:

```bash
dotnet run --project src/ScorePlusTwo.Pipeline -- --fixture tests/fixtures/2026-09-03.json --fecha 03-09-2026
```

## Revisión humana: términos ambiguos y overrides

Dos mecanismos relacionados para cuando una señal automática no alcanza por
sí sola:

**Términos ambiguos** (`config/criterios.json`, campo `terminos_ambiguos`
por rubro — hoy solo `["plataforma"]` en `ti`): un término marcado ahí
colisiona con negocios que no son el rubro (ej. "plataforma" aparece tanto
en servicios TI reales como en suscripciones de puro bien). Si el ÚNICO
término que matchea un rubro está en esta lista, la candidata no se
promueve a Prioritarias aunque UNSPSC y región digan que sí — cae en
Secundarias con `estado_flujo: "revision_ambigua"`, con `rubro_match`/
`termino_match` igual poblados. Si matchea además un término no ambiguo del
mismo rubro, ese gana y el resultado es idéntico a como sería sin este
campo — la ambigüedad nunca descarta nada, solo baja la confianza de una
señal única.

**`data/overrides.json`** (`Dictionary<CodigoExterno, override>`): decisión
humana que fuerza un código a Prioritarias o Secundarias, sin importar lo
que diga la clasificación automática. Se aplica en cada corrida, sobre las
listas ya fusionadas, antes de persistirlas — y **nunca expira**: si un
override ya está en el destino correcto, la corrida siguiente es un no-op.
La única forma de revertirlo es escribir un override nuevo o borrar la
entrada a mano.

```json
{
  "85-41-LE26": {
    "lista_destino": "prioritarias",
    "revisado": true,
    "observado_en": "2026-09-19T12:00:00Z"
  }
}
```

**Pestaña "Revisión" del tablero** (`docs/index.html`/`docs/app.js`): junta
las Secundarias con `unspsc_estado: "revision_manual"` o
`estado_flujo: "revision_ambigua"` — hoy la única forma de verlas sin bajar
el CSV completo. Cada fila trae dos botones ("Mover a Prioritarias" /
"Confirmar en Secundarias") que escriben directo en `data/overrides.json`
vía la API REST de GitHub (`PUT contents/data/overrides.json`), sin backend
propio. La primera vez que se usa un botón, el navegador pide un token de
GitHub y lo guarda en `localStorage` de ese navegador — el token nunca se
envía a otro destino que no sea `api.github.com`. **El cambio real de
lista no es instantáneo**: la fila desaparece de la cola de inmediato, pero
el override recién se aplica en la próxima corrida nocturna del pipeline
(o disparando el workflow manualmente). Cuando no queda ninguna fila
pendiente, la pestaña muestra explícitamente "Todo al día — no hay nada
pendiente de revisión." — nunca una tabla vacía sin explicación, que se
leería como la pestaña rota en vez de como una cola resuelta.

**Qué token usar — importante:** generar un **fine-grained personal access
token** (GitHub → Settings → Developer settings → Fine-grained tokens), no
un token clásico de scope `repo` completo. Configurarlo con:
- **Repository access**: "Only select repositories" → este repositorio
  únicamente.
- **Permissions**: `Contents: Read and write` y `Actions: Read and write`
  — nada más. `Actions` es necesaria para la pestaña "Consulta" (ver más
  abajo), que dispara `workflow_dispatch`; si el token es de antes de esa
  pestaña y solo tiene `Contents`, "Revisión" sigue funcionando pero
  "Consulta" fallará al disparar — hay que regenerar el token agregando el
  permiso.
- **Expiration**: una fecha concreta, no "No expiration".

La diferencia importa: un token clásico de scope `repo` completo puede leer
y escribir en TODOS los repositorios (públicos y privados) de la cuenta que
lo generó. Si el tablero tiene un bug algún día, o el token queda expuesto
por accidente (historial del navegador, extensión maliciosa, captura de
pantalla), un fine-grained token acotado a este repo y con fecha de
expiración limita el daño a "alguien puede escribir en este repo hasta tal
fecha" — un token clásico de scope completo lo expone todo, sin fecha
límite, hasta que alguien lo revoque a mano.

**Detalle expandible:** cada fila de la pestaña Revisión tiene un enlace
"Ver detalle" que expande un panel con datos que la tabla compacta no
muestra (descripción completa, organismo, ítems UNSPSC con su categoría
resuelta, etc.). Qué campos se muestran por default y cuáles quedan detrás
de "Mostrar más campos" es editable en `config/panel-revision.json` (mismo
patrón que `config/criterios.json`) — el cambio se refleja en el tablero
en la siguiente corrida del pipeline, no hace falta tocar código:

```json
{
  "campos_visibles": ["descripcion", "fecha_cierre", "organismo", "region", "monto", "cantidad_reclamos"],
  "campos_ocultos_por_default": ["items_unspsc", "comuna", "sub_contratacion", "prohibicion_contratacion", "tipo_pago"]
}
```

`sub_contratacion` y `tipo_pago` son códigos numéricos que trae la API sin
diccionario de traducción disponible (ej. `"4"`, `"1"`) — se muestran tal
cual, por eso quedan ocultos por default.

## Consulta puntual de un código de licitación

Pestaña "Consulta" del tablero: un campo de texto para pegar un código
externo (ej. `734-50-LE26`) y un botón "Consultar" que trae su detalle
completo, incluyendo la metadata de adjudicación si el código ya está
adjudicado (fecha del acta, número de acta, cantidad de oferentes, y un
link al acta real en Mercado Público) — sin esperar a que ese código pase
por el flujo diario ni aparezca en ninguna de las tres listas.

**Requiere el mismo token de la pestaña "Revisión"**, con el permiso
adicional `Actions: Read and write` (ver arriba) — la pestaña necesita
poder disparar el workflow, no solo leer/escribir archivos.

**Cómo funciona:** primero intenta leer `data/consultas/{codigo}.json` vía
la API de contenidos de GitHub — si ese código ya se consultó antes, lo
muestra al instante, sin disparar nada. Si no existe, dispara
`workflow_dispatch` sobre `diario.yml` con el código (equivalente a correr
`--consultar-licitacion <codigo>`, ver más abajo) y hace polling del
estado del run cada 10-15 segundos; cuando termina, vuelve a leer el
archivo (que el propio workflow ya commiteó) y lo muestra.

**Una consulta nueva puede tardar 1-2 minutos — no es instantánea.** El
tiempo real es la suma de la cola de `workflow_dispatch` en Actions más el
tiempo de build+ejecución del pipeline (unos 20-30 segundos, medido en
corridas reales) — a diferencia del cron nocturno, `workflow_dispatch` no
sufre el atraso documentado más abajo (`diario.yml`, 1h52-4h58), pero
sigue sin ser instantáneo.

**Sobre el campo "Adjudicación": no trae el nombre del ganador.** La API
de Mercado Público solo entrega metadata del proceso (fecha del acta,
número de acta, cantidad de oferentes) y un link a la ficha real del acta
en `mercadopublico.cl` — para ver quién ganó hay que abrir ese link a
mano. Deliberadamente no se intenta traer el nombre del ganador
automáticamente (scrapeando esa página): es el mismo riesgo ya conocido
con `url_ficha` en el dashboard (URLs de ese dominio con querystring de
sesión que no siempre resuelven al recurso pedido) y el valor no
justifica el mantenimiento para una consulta puntual y poco frecuente. Si
en algún momento conocer ganadores de forma sistemática (no puntual) se
vuelve una necesidad recurrente, la vía correcta es el bulk de datos
abiertos OCDS de ChileCompra (histórico de adjudicaciones estructurado,
sin scraping) — no esta pestaña.

**Modo de servidor** (`--consultar-licitacion <codigo>`), para quien
prefiera correrlo directo en vez de usar el botón:

```bash
dotnet run --project src/ScorePlusTwo.Pipeline -- --consultar-licitacion 734-50-LE26
```

Requiere `MP_TICKET` real (sin equivalente a `--fixture`) y escribe/
sobrescribe `data/consultas/{codigo saneado}.json` — persistente, sirve de
cache para la próxima vez que se consulte el mismo código, sea desde el
tablero o desde la línea de comandos.

## Re-filtrado manual del histórico acumulado

`data/raw/` guarda el crudo completo de cada lote diario y cada barrido
semanal `activas`, sin filtrar. Eso permite, cuando aparece un cliente de un
rubro que hoy no está en `config/criterios.json`, re-filtrar todo lo ya
acumulado con criterios distintos y mostrar resultados el mismo día, sin
esperar a que ese rubro se agregue a la configuración y se junten semanas de
datos nuevos. También sirve para probar un cambio de criterios contra el
histórico antes de aplicarlo a producción.

Este modo es de **solo lectura** sobre el estado de producción: lee
`data/raw/` y escribe únicamente el CSV de salida. Nunca toca
`data/candidatas.json`, `data/secundarias.json`, `data/tramo_bajo.json`,
`data/informes.json`, `data/eventos.json` ni `docs/data.json`. El CSV solo
recoge lo que habría entrado a **Prioritarias** con el criterios alternativo
— es la lista relevante para "qué le mostraría a un cliente de este rubro".

```bash
dotnet run --project src/ScorePlusTwo.Pipeline -- \
  --refiltrar \
  --criterios config/criterios-construccion.json \
  --desde 2026-08-01 \
  --salida reportes/candidatas-construccion.csv
```

- `--criterios <ruta>` (obligatorio): archivo de criterios alternativo, con
  el mismo formato que `config/criterios.json`.
- `--desde YYYY-MM-DD` (opcional): sólo procesa lotes de `data/raw/` con
  fecha igual o posterior. Si se omite, procesa todo el histórico.
- `--salida <ruta>` (opcional): ruta del CSV de salida.

El CSV resultante trae una fila por candidata (deduplicada por
`CodigoExterno`, quedándose con la aparición más reciente si el mismo código
aparece en más de un lote) con las columnas `codigo, nombre, tipo,
rubro_match, termino_match, fecha_cierre, archivo_origen, fecha_lote` — las
dos últimas indican de qué archivo de `data/raw/` salió cada resultado.

### `config/criterios-descartes.json`: medir el efecto de un rubro nuevo sobre el histórico

Antes de que el filtro clasificara en tres listas, un registro sin rubro
match se descartaba en silencio y no había forma de verlo — por eso se creó
este archivo, con un rubro comodín que matchea todo (cualquier nombre en
español contiene alguna vocal), para exponer ese conjunto vía `--refiltrar`.

Hoy ese conjunto ya es visible en producción sin ningún paso extra: es
exactamente **`data/secundarias.json`** (Lista B). `criterios-descartes.json`
sigue siendo útil para un caso más específico — medir, sobre el histórico ya
acumulado, cuántas licitaciones habría capturado un rubro **hipotético
nuevo** que aún no existe en `config/criterios.json` (ver ejemplo de
`--refiltrar` más arriba, con un archivo de criterios que sí define ese
rubro). Es una copia de `config/criterios.json` con `tipos`, `estados` y
`descarte_duro` idénticos — solo cambia `rubros`:

```json
"rubros": [
  { "id": "todo", "prioridad": "alta", "terminos": ["a", "e", "i", "o", "u"] }
]
```

```bash
dotnet run --project src/ScorePlusTwo.Pipeline -- \
  --refiltrar \
  --criterios config/criterios-descartes.json \
  --salida descartes.csv
```

El CSV de salida es un artefacto de análisis puntual — no se commitea al
repo (`descartes*.csv` está en `.gitignore`), a diferencia de
`config/criterios-descartes.json` en sí.
