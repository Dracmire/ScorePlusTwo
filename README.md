# ScorePlusTwo
A Business intelligence monitoring and filtering for B2G

## Pipeline diario

Corre automáticamente todos los días vía GitHub Actions
(`.github/workflows/diario.yml`): descarga el lote diario de licitaciones de
Mercado Público, lo filtra contra `config/criterios.json` y actualiza
`data/informes.json`, `data/eventos.json` y el tablero en `docs/`.

El filtro clasifica cada registro en una de tres listas — nunca en una sola,
y solo obras públicas/suministros (`descarte_duro`) desaparecen sin rastro:

- **Prioritarias** (`data/candidatas.json`): matchean un rubro de
  `prioridad: "alta"` en `config/criterios.json`. Es lo que va al tablero.
- **Secundarias** (`data/secundarias.json`): sobreviven estado+tipo+
  descarte_duro pero no entran a Prioritarias (rubro de prioridad
  `"secundaria"`, sin ningún rubro, o bloqueadas por `exclusiones_rubro` —
  ej. "software"/"servidor", que distinguen compra de bien de servicio).
  Es el inventario para prospectar rubros que todavía no se atienden.
- **Tramo bajo** (`data/tramo_bajo.json`): tipo `L1`, aceptado pero fuera de
  la clasificación de rubro — no se mezcla con el resto, es opción solo si
  aparece un cliente que la tome.

Para correrlo manualmente sobre un fixture local, sin red ni `MP_TICKET`:

```bash
dotnet run --project src/ScorePlusTwo.Pipeline -- --fixture tests/fixtures/2026-09-03.json --fecha 03-09-2026
```

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
rubro). Es una copia de `config/criterios.json` con `tipos`, `estados`,
`descarte_duro` y `exclusiones_rubro` idénticos — solo cambia `rubros`:

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
