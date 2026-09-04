# Especificación técnica — Sistema de descubrimiento y filtrado

**Estado:** pre-beta · **Alcance de este documento:** MVP (Descubrimiento → Filtro)
**Última revisión:** septiembre 2026

---

## 1. Propósito

Detectar diariamente, sin intervención humana, las licitaciones de Mercado Público
que corresponden a los rubros y regiones de interés, y presentarlas para triage
manual en un tablero accesible de forma remota.

El sistema **no** decide. Reduce ~1.200 licitaciones diarias a ~3 candidatas
para que una persona decida sobre esas tres.

### Lo que no hace el MVP

Extracción de anexos, generación de scorecard, envío a clientes, portal de cliente,
análisis de competencia. Todo eso depende de datos que el MVP empieza a acumular.

---

## 2. Restricciones de la fuente

Verificadas contra la API de ChileCompra:

| Restricción | Consecuencia de diseño |
|---|---|
| El listado diario solo trae código, nombre, estado y fecha de cierre | El filtro de rubro opera sobre el nombre. El resto requiere una llamada por licitación |
| No hay filtro por texto, rubro ni región en la API | El filtrado es 100% del lado nuestro |
| `fecha=X` devuelve actividad de ese día, no lo vigente | Se necesita `estado=activas` para el estado real del mercado |
| Cuota de 10.000 peticiones/día por ticket | Suficiente, pero obliga a pedir detalle solo de sobrevivientes |
| ChileCompra recomienda consultas pesadas entre 22:00 y 07:00 | El job corre a las 06:00 hora Chile |
| La API está en beta y ChileCompra licita su reemplazo | Aislar el acceso en un solo módulo, reemplazable |

### Códigos relevantes

**Estados:** 5 Publicada · 6 Cerrada · 7 Desierta · 8 Adjudicada · 18 Revocada · 19 Suspendida

**Tipos objetivo:** LE (100–1.000 UTM) · LP (>1.000 UTM) · LR (>5.000 UTM) · LS (servicios personales especializados)

**Tipos descartados:** L1 (<100 UTM, bajo el piso de monto) y todo trato directo,
orden de compra, licitación privada y obra pública (D1, C1, F2, G1, R1, R2, CO, O1, E2, B2, I2, SE).

---

## 3. Arquitectura

```
GitHub Actions (cron 06:00 CLT)
        │
        ├── fetch.py ──────► data/raw/YYYY-MM-DD.json      (lote crudo, íntegro)
        │                    data/adjudicadas/YYYY-MM.json  (histórico)
        │
        ├── filtro.py ─────► data/candidatas.json
        │                    data/informes.json
        │
        └── build.py ──────► docs/data.json  ──► GitHub Pages (tablero)
                                                       │
                                                  triage manual
```

**Repositorio privado + GitHub Pro (~$4/mes).** Pages sobre repo privado requiere
Pro; el cómputo (~2 min/día) cabe holgado en la cuota gratuita.

**Lenguaje: Python.** Sin paso de build ni restore en Actions, y el pipeline son
~200 líneas. Portable a C#/.NET si el sistema migra a Azure — la lógica de filtro
es pura y no depende del lenguaje.

**Persistencia: archivos JSON versionados en el repo.** Cada corrida es un commit.
Esto da gratis lo que una base de datos no da: historial completo de cómo cambiaron
los criterios y qué produjo cada versión del filtro. A este volumen (~5 MB/año)
no hay razón para más.

**El ticket va en GitHub Secrets.** Nunca en el repo.

---

## 4. Modelo de datos

### `config/criterios.json` — versionado, editable sin tocar código

```json
{
  "version": "2026-09-04",
  "tipos": ["LE", "LP", "LR", "LS"],
  "estados": [5],
  "regiones": ["Metropolitana", "Valparaíso", "Coquimbo"],
  "rubros": [
    { "id": "compliance", "activo": true, "terminos": ["auditor", "..."] }
  ],
  "exclusiones": ["insumo", "medicamento", "..."]
}
```

### `data/raw/YYYY-MM-DD.json`

Lote crudo íntegro, sin filtrar. **Se guarda completo por decisión explícita:**
el Victory Card y el análisis de competencia futuros necesitan histórico que la
API no permite reconstruir hacia atrás.

### `data/candidatas.json`

```
codigo, nombre, tipo, fecha_cierre, fecha_lote,
rubro_match, termino_match, region, organismo,
estado_flujo, notas, cliente_asignado
```

`estado_flujo`: `pendiente → candidata → scorecard → enviada → tomada | descartada`

### `data/informes.json`

Serie diaria: `fecha, total, tras_estado, tras_tipo, tras_region, excluidas, candidatas, nuevas`

### `data/organismos.json`

Cache `código → nombre, región`. Se puebla incrementalmente. Ver §5.3.

### `data/eventos.json` — append-only

`timestamp, actor, accion, codigo, detalle`

Registro inmutable. En el MVP solo captura acciones propias de triage. Existe desde
ahora porque el "take it / leave it" de Stage 3 lo va a necesitar como evidencia
contractual, y retrofitear un registro de auditoría es caro.

---

## 5. Pipeline

### 5.1 Ingesta

Dos consultas diarias:

1. `?fecha=DDMMAAAA` del **día anterior** (completo, ya cerrado)
2. `?estado=adjudicada&fecha=DDMMAAAA` — construye el histórico de adjudicaciones
   para el scoring de comprador y el análisis de competencia futuros

Semanalmente, más una vez al inicializar:

3. `?estado=activas` — **estado real del mercado.** Resuelve el problema detectado
   en la validación: una licitación publicada el 24-ago con cierre el 4-sep no
   aparece en ningún lote diario posterior a su publicación. Sin esta consulta,
   el sistema es ciego a todo lo que sigue abierto pero no tuvo movimiento hoy.

Dedupe por `CodigoExterno` contra lo ya conocido.

### 5.2 Filtro

Orden estricto, de más barato a más caro:

```
1. Estado ∈ criterios.estados          1.172 → ~420
2. Tipo ∈ criterios.tipos                420 → ~230
3. Exclusiones (ganan siempre)           230 → ~215
4. Términos de rubro sobre el nombre     215 → ~6
5. Región (solo sobre sobrevivientes)      6 → ~3
```

**El orden no es negociable.** Filtrar por región primero exigiría una llamada de
detalle por cada una de las 230; hacerlo al final son ~6 llamadas. Misma lógica
para cualquier enriquecimiento futuro.

### 5.3 Región

El listado no trae región. Estrategia en dos tiempos:

- El prefijo de `CodigoExterno` es el código del organismo comprador
- Si está en `organismos.json`, la región sale del cache — costo cero
- Si no está, una llamada de detalle lo resuelve y queda cacheado para siempre

Los organismos no cambian de región. Tras ~1 mes de operación el cache cubre
prácticamente todo y el filtro regional deja de costar llamadas.

**Regla de seguridad:** si la región no se puede determinar, la candidata **pasa**.
Es preferible revisar una de más que perder una válida.

### 5.4 Tablero

Página estática en Pages, sin backend. Lee `docs/data.json`.

- Lista de candidatas con estado de flujo y días para el cierre
- Link directo a la ficha en Mercado Público
- Serie de calibración: tasa de rubro por día
- Vista de solo lectura pública para demostraciones a asociados

**El triage escribe de vuelta al repo.** En el MVP, vía edición del JSON o un
formulario que abre un issue. Se resuelve bien en F2; no bloquea el MVP.

---

## 6. Fases de automatización

| Fase | Alcance | Estado | Depende de |
|---|---|---|---|
| **F0** | Prototipo manual: pegar JSON, filtrar en el navegador | Hecho | — |
| **F1 · MVP** | Fetch diario + `activas` + filtro + acumulación de crudo + tablero | **En construcción** | Ticket, repo |
| **F2** | Enriquecimiento: detalle de sobrevivientes, cache de organismos, filtro regional efectivo, triage con escritura | Siguiente | F1 con datos reales |
| **F3** | Refinamiento del filtro con métricas acumuladas: ajuste de términos contra tasa de acierto medida | Siguiente | ≥1 mes de F1 |
| **F4** | Esqueleto de scorecard automático: ficha, montos, plazos, historial de pago del comprador | MVD | F2 |
| **F5** | Extracción de anexos y matriz de puntaje | MVD | Resolver descarga de documentos |
| **F6** | Entrega: borrador de correo, seguimiento de envío | MVD | F4 |
| **F7** | Victory Card e histórico para licitar | Post-MVD | ≥6 meses de adjudicadas |
| **F8** | Análisis de competencia post-cierre | Post-MVD | F7 |

**Ruta acordada:** F1 → F2 → F3 → evaluar → MVD (F4–F6).

El circuito completo es el MVD, pero el filtro tiene que estar afinado antes.
Un scorecard sobre una candidata mal seleccionada no vale nada.

---

## 7. Criterios de éxito del MVP

| Métrica | Umbral | Qué indica si falla |
|---|---|---|
| Corridas diarias exitosas | >95% en 30 días | Fragilidad de la API o del job |
| Candidatas/día | 1–5 | <1: filtro muy estrecho. >10: muy laxo |
| Falsos positivos en triage | <40% | Faltan exclusiones |
| Falsos negativos detectados | Registrar todos | Faltan términos de rubro |
| Cobertura del cache de organismos | >90% al mes | Estrategia de región no funciona |

**La métrica que decide el negocio, no el software:** tasa de rubro sostenida.
Medida sobre un día dio ~1,7%. Si tras 30 días sigue bajo 2%, el rubro
Legal-Información-Compliance por sí solo no sostiene el flujo en B2G, y la
decisión pasa a ser ampliar rubros o reponderar hacia el canal privado.

---

## 8. Riesgos

| Riesgo | Mitigación |
|---|---|
| ChileCompra reemplaza la API | Acceso aislado en un módulo. El crudo acumulado sobrevive al cambio |
| Falsos negativos por nomenclatura | Barrido semanal manual sobre `tras_tipo` sin filtro de rubro, para detectar lo que se escapó |
| Filtro sobreajustado a un caso | Los criterios son versionados; cada cambio es reversible y auditable |
| El repo crece sin control | Comprimir `raw/` mensualmente. ~5 MB/año sin comprimir |
| Ticket revocado por uso excesivo | 3 peticiones/día contra una cuota de 10.000 |

---

## 9. Decisiones tomadas y su motivo

**Guardar el lote crudo completo.** Las fases F7 y F8 necesitan histórico que la
API no reconstruye hacia atrás. El costo de guardarlo es trivial; el de no tenerlo
es no poder construir el producto diferenciador.

**Registro de eventos desde el día uno.** Stage 3 lo requiere como evidencia
contractual del "take it". Agregarlo después obliga a reescribir el flujo.

**Sin servidor hasta salir de beta.** No está validado que el servicio lo necesite.
GitHub Actions cubre cron, cómputo y hosting a costo casi cero, y el pipeline es
código puro que migra a Azure o AWS sin reescritura si el volumen lo justifica.

**El cliente nunca ve el pipeline.** El valor está en las 64 candidatas descartadas,
no en la que pasa. Exponer el feed crudo convierte el servicio en un buscador y
destruye la diferenciación. Stage 2 entrega scorecards y resultados, no búsqueda.
