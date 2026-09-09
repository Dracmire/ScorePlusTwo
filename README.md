# ScorePlusTwo
A Business intelligence monitoring and filtering for B2G

## Pipeline diario

Corre automáticamente todos los días vía GitHub Actions
(`.github/workflows/diario.yml`): descarga el lote diario de licitaciones de
Mercado Público, lo filtra contra `config/criterios.json` y actualiza
`data/candidatas.json`, `data/informes.json`, `data/eventos.json` y el
tablero en `docs/`.

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
`data/candidatas.json`, `data/informes.json`, `data/eventos.json` ni
`docs/data.json`.

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

### Medir falsos negativos del rubro: `config/criterios-descartes.json`

Un registro que sobrevive estado/tipo/exclusiones pero no matchea ningún
término de rubro se descarta en silencio — no queda registrado en
`candidatas.json` ni en `observaciones` (eso es solo para rubros `activo:
false`, como `ia`). No hay forma de ver ese conjunto con la configuración de
producción.

`config/criterios-descartes.json` es un criterios alternativo pensado para
eso: es una copia de `config/criterios.json` con `tipos`, `estados`,
`regiones` y `exclusiones` idénticos, pero con un único rubro comodín que
matchea todo (como cualquier nombre en español contiene alguna vocal):

```json
"rubros": [
  { "id": "todo", "activo": true, "terminos": ["a", "e", "i", "o", "u"] }
]
```

Corriendo `--refiltrar` con este archivo, el CSV resultante es exactamente
"todo lo que sobrevivió estado+tipo+exclusiones" — el conjunto completo de lo
que la etapa de rubro real está descartando sin dejar rastro:

```bash
dotnet run --project src/ScorePlusTwo.Pipeline -- \
  --refiltrar \
  --criterios config/criterios-descartes.json \
  --salida descartes.csv
```

El CSV de salida es un artefacto de análisis puntual — no se commitea al
repo, a diferencia de `config/criterios-descartes.json` en sí.
