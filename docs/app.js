(function () {
  "use strict";

  // Pestaña "Revisión" (2026-09-19): escribe directo a la API de GitHub
  // (contents API), sin backend propio — ver docs/app.js más abajo y
  // data/overrides.json. Repo hardcodeado porque este tablero solo sirve a
  // este proyecto.
  //
  // Pestaña "Consulta" (2026-09-22) reusa el mismo token, pero además de
  // Contents necesita disparar workflow_dispatch (ver dispararConsulta) —
  // por eso pedirToken() pide los dos permisos desde ahora, aunque un
  // token viejo con solo Contents siga sirviendo para Revisión.
  var GITHUB_REPO = "Dracmire/ScorePlusTwo";
  var GITHUB_WORKFLOW = "diario.yml";
  var TOKEN_KEY = "gh_token";

  function githubToken() {
    try {
      return localStorage.getItem(TOKEN_KEY);
    } catch (e) {
      return null; // localStorage bloqueado (navegación privada, etc.)
    }
  }

  // El token nunca se envía a ningún lado salvo a api.github.com — se pide
  // una sola vez y se guarda en localStorage de este navegador.
  function pedirToken() {
    var token = prompt(
      "Token de GitHub para las pestañas Revisión y Consulta (ver README —\n" +
      "usa un fine-grained token con expiración, acotado a este repo, con\n" +
      "los permisos 'Contents: Read and write' y 'Actions: Read and write').\n" +
      "Se guarda solo en este navegador y solo se envía a la API de GitHub."
    );
    if (token) {
      try {
        localStorage.setItem(TOKEN_KEY, token);
      } catch (e) {
        // sin persistencia disponible: igual sirve para esta sesión de página
      }
    }
    return token;
  }

  function utf8ToBase64(texto) {
    return btoa(unescape(encodeURIComponent(texto)));
  }

  function base64ToUtf8(b64) {
    return decodeURIComponent(escape(atob(b64.replace(/\n/g, ""))));
  }

  // Lee data/overrides.json vía la Contents API (no raw.githubusercontent.com:
  // docs/ es lo único que sirve GitHub Pages, data/ queda fuera). Sin token
  // funciona igual si el repo es público; el archivo puede no existir
  // todavía (404 = sin overrides, no un error).
  function leerOverrides() {
    var headers = { Accept: "application/vnd.github+json" };
    var token = githubToken();
    if (token) headers.Authorization = "Bearer " + token;

    return fetch("https://api.github.com/repos/" + GITHUB_REPO + "/contents/data/overrides.json", { headers: headers })
      .then(function (respuesta) {
        if (respuesta.status === 404) return { overrides: {}, sha: null };
        if (!respuesta.ok) throw new Error("No se pudo leer overrides.json (HTTP " + respuesta.status + ")");
        return respuesta.json().then(function (cuerpo) {
          var contenido = cuerpo.content ? base64ToUtf8(cuerpo.content) : "{}";
          return { overrides: JSON.parse(contenido || "{}"), sha: cuerpo.sha };
        });
      });
  }

  // PUT directo a contents/data/overrides.json — el pipeline de la
  // siguiente corrida nocturna es quien realmente mueve la candidata entre
  // listas (ver AplicadorOverrides.cs), esto solo escribe la decisión.
  function guardarOverride(codigo, listaDestino) {
    var token = githubToken() || pedirToken();
    if (!token) return Promise.reject(new Error("Sin token, no se guardó."));

    return leerOverrides().then(function (actual) {
      actual.overrides[codigo] = {
        lista_destino: listaDestino,
        revisado: true,
        observado_en: new Date().toISOString(),
      };

      var cuerpo = {
        message: "override: " + codigo + " -> " + listaDestino,
        content: utf8ToBase64(JSON.stringify(actual.overrides, null, 2)),
        branch: "main",
      };
      if (actual.sha) cuerpo.sha = actual.sha;

      return fetch("https://api.github.com/repos/" + GITHUB_REPO + "/contents/data/overrides.json", {
        method: "PUT",
        headers: {
          Accept: "application/vnd.github+json",
          Authorization: "Bearer " + token,
          "Content-Type": "application/json",
        },
        body: JSON.stringify(cuerpo),
      });
    }).then(function (respuesta) {
      if (respuesta.ok) return respuesta.json();
      return respuesta.json().catch(function () { return {}; }).then(function (error) {
        throw new Error(error.message || ("Error HTTP " + respuesta.status + " al guardar."));
      });
    });
  }

  // Pestaña "Consulta" (2026-09-22): mismo saneo de nombre de archivo que
  // Program.EjecutarConsultarLicitacionAsync — si no calzan, esta pestaña
  // nunca encuentra el archivo que escribió --consultar-licitacion.
  function sanearCodigoArchivo(codigo) {
    return codigo.replace(/[/\\:*?"<>|\s]/g, "_");
  }

  // Lee data/consultas/{codigo}.json vía la Contents API — null si no
  // existe todavía (404, no un error: significa "nunca se consultó este
  // código, o el resultado se perdió al no ser un archivo tracked" — en la
  // práctica siempre va a existir tras el primer --consultar-licitacion
  // exitoso, porque ese modo lo commitea).
  function leerConsulta(codigo) {
    var headers = { Accept: "application/vnd.github+json" };
    var token = githubToken();
    if (token) headers.Authorization = "Bearer " + token;

    var ruta = "data/consultas/" + sanearCodigoArchivo(codigo) + ".json";
    return fetch("https://api.github.com/repos/" + GITHUB_REPO + "/contents/" + ruta, { headers: headers })
      .then(function (respuesta) {
        if (respuesta.status === 404) return null;
        if (!respuesta.ok) throw new Error("No se pudo leer la consulta (HTTP " + respuesta.status + ")");
        return respuesta.json().then(function (cuerpo) {
          return JSON.parse(base64ToUtf8(cuerpo.content));
        });
      });
  }

  // POST a la API de Actions para disparar diario.yml con
  // consultar_licitacion=codigo — primera vez que este tablero dispara un
  // workflow en vez de solo escribir un archivo (por eso el token
  // necesita también Actions: Read and write, ver pedirToken).
  function dispararConsulta(codigo) {
    var token = githubToken() || pedirToken();
    if (!token) return Promise.reject(new Error("Sin token, no se pudo disparar la consulta."));

    return fetch("https://api.github.com/repos/" + GITHUB_REPO + "/actions/workflows/" + GITHUB_WORKFLOW + "/dispatches", {
      method: "POST",
      headers: {
        Accept: "application/vnd.github+json",
        Authorization: "Bearer " + token,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ ref: "main", inputs: { consultar_licitacion: codigo } }),
    }).then(function (respuesta) {
      if (respuesta.status === 204) return;

      // 403 (a veces 404, para no revelar el recurso a un token sin
      // permiso) es lo que devuelve GitHub cuando el fine-grained token
      // no tiene 'Actions: Read and write' — confirmado con el error real
      // que reportó un usuario con un token de antes de que ese permiso
      // se documentara. El texto crudo de GitHub ("Resource not
      // accessible by personal access token") no dice qué hacer; acá se
      // reemplaza por un mensaje accionable, y se limpia el token
      // guardado para que el siguiente intento vuelva a pedirlo — si no,
      // el usuario regenera el token en GitHub pero el tablero sigue
      // reintentando en silencio con el viejo hasta que borre
      // localStorage a mano.
      if (respuesta.status === 403 || respuesta.status === 404) {
        try { localStorage.removeItem(TOKEN_KEY); } catch (e) { /* sin persistencia disponible */ }
        throw new Error(
          "Tu token no tiene permiso para disparar Actions. Regenéralo en GitHub " +
          "(Settings → Developer settings → Fine-grained tokens) agregando el permiso " +
          "'Actions: Read and write', además de 'Contents: Read and write' que ya tiene. " +
          "Vuelve a intentar — te va a pedir el token de nuevo."
        );
      }

      return respuesta.json().catch(function () { return {}; }).then(function (error) {
        throw new Error(error.message || ("Error HTTP " + respuesta.status + " al disparar la consulta."));
      });
    });
  }

  // Polling cada 12s (dentro del rango 10-15s pedido) sobre el run de
  // workflow_dispatch más reciente que sea posterior a `desde` — así no se
  // confunde con un run manual anterior que ya estaba en la lista. El
  // límite de reintentos deja ~5 minutos de margen: el atraso medido del
  // cron (1h52-4h58, ver diario.yml) no aplica acá porque workflow_dispatch
  // no compite con la cola de `schedule`, pero igual no es instantáneo.
  function esperarRunDeConsulta(token, desde) {
    var headers = { Accept: "application/vnd.github+json" };
    if (token) headers.Authorization = "Bearer " + token;
    var url = "https://api.github.com/repos/" + GITHUB_REPO + "/actions/workflows/" + GITHUB_WORKFLOW + "/runs?event=workflow_dispatch&per_page=5";

    return new Promise(function (resolve, reject) {
      var intentosRestantes = 24;

      function unIntento() {
        fetch(url, { headers: headers })
          .then(function (respuesta) {
            if (!respuesta.ok) throw new Error("Error al consultar el estado del workflow (HTTP " + respuesta.status + ")");
            return respuesta.json();
          })
          .then(function (cuerpo) {
            var runs = cuerpo.workflow_runs || [];
            var run = runs.filter(function (r) { return new Date(r.created_at) >= desde; })[0];

            if (run && run.status === "completed") {
              resolve(run);
              return;
            }

            if (intentosRestantes-- <= 0) {
              reject(new Error("La consulta sigue en curso en Actions — intenta leerla de nuevo en un momento."));
              return;
            }

            setTimeout(unIntento, 12000);
          })
          .catch(reject);
      }

      unIntento();
    });
  }

  function formatearFecha(iso) {
    if (!iso) return "—";
    var d = new Date(iso);
    return d.toLocaleDateString("es-CL", { year: "numeric", month: "2-digit", day: "2-digit" });
  }

  function escaparHtml(texto) {
    var div = document.createElement("div");
    div.textContent = texto == null ? "" : String(texto);
    return div.innerHTML;
  }

  function renderCodigo(candidata) {
    if (candidata.url_ficha) {
      return '<a class="codigo" href="' + candidata.url_ficha + '" target="_blank" rel="noopener">' +
        escaparHtml(candidata.codigo) + "</a>";
    }
    // Sin link verificado (ver GeneradorDashboard.cs): texto plano copiable.
    return '<span class="codigo" data-copiar="' + escaparHtml(candidata.codigo) + '" title="Click para copiar">' +
      escaparHtml(candidata.codigo) + "</span>";
  }

  // Los tipos privados (CO/B2/E2/H2/I2, ver Criterios.TiposPrivados) siempre
  // caen en Secundarias sin pasar por clasificación de rubro — el badge es
  // lo que permite encontrarlos ahí para la revisión de dos semanas.
  function renderTipo(candidata) {
    var tipo = escaparHtml(candidata.tipo);
    if (!candidata.tipo_privado) return tipo;
    return tipo + ' <span class="badge badge-normal" title="Tipo privado: siempre va a Secundarias, en revisión">privado</span>';
  }

  function renderRubro(candidata) {
    // Secundarias sin ningún rubro match (inventario crudo de prospección) y
    // tramo bajo (L1, nunca pasa por clasificación de rubro) no tienen nada
    // que mostrar acá.
    if (!candidata.rubro_match) return '<span class="vacio">—</span>';
    return escaparHtml(candidata.rubro_match) + " · " + escaparHtml(candidata.termino_match);
  }

  // Etiquetas legibles para UnspscEstado (ver ClasificadorUnspsc.cs) — el
  // valor crudo llega en snake_case (JsonOpciones.Persistencia).
  var ETIQUETAS_UNSPSC = {
    servicio: "Servicio",
    revision_manual: "Revisión manual",
    bien: "Bien",
    sin_resolver: "Sin resolver",
    pendiente_enriquecimiento: "Pendiente",
  };

  function renderUnspscEstado(candidata) {
    var etiqueta = ETIQUETAS_UNSPSC[candidata.unspsc_estado];
    if (!etiqueta) return '<span class="vacio">—</span>';
    return escaparHtml(etiqueta);
  }

  function renderRegion(candidata) {
    if (!candidata.region) return '<span class="vacio">—</span>';
    return escaparHtml(candidata.region);
  }

  // Por qué una fila cae en la cola de revisión — los motivos posibles no
  // son excluyentes en teoría, pero en la práctica cada candidata trae
  // como mucho uno (ver FiltroLicitaciones/Program.EjecutarReevaluarInventarioAsync).
  function razonRevision(candidata) {
    var razones = [];
    if (candidata.unspsc_estado === "revision_manual") razones.push("UNSPSC ambiguo en modalidad");
    if (candidata.estado_flujo === "revision_ambigua") {
      razones.push("rubro ambiguo (" + escaparHtml(candidata.termino_match || "") + ")");
    }
    if (candidata.estado_flujo === "revision_degradada") {
      razones.push("dejó de calificar para Prioritarias (reevaluación de inventario)");
    }
    return razones.length ? razones.join(" · ") : '<span class="vacio">—</span>';
  }

  // Calculado en el navegador, SIEMPRE contra la fecha de hoy del cliente —
  // nunca leído de data.json. dias_para_cierre solía congelarse al generar
  // el JSON (Math.ceil de una resta en milisegundos desde el momento de esa
  // corrida) y envejecía sin aviso si el pipeline no corría un día o el
  // navegador cacheaba el archivo: un 08-09 generado el 05-09 daba "3 días"
  // en vez de los "2 días" reales al mirarlo el 06-09. Por eso se trunca
  // tanto el cierre como "hoy" a medianoche antes de restar: la cuenta debe
  // coincidir con cómo cuenta un humano ("el 8 menos el 6 son 2"), no con
  // cuántas horas exactas faltan.
  function diasParaCierre(fechaCierreIso) {
    if (!fechaCierreIso) return null;
    var cierre = new Date(fechaCierreIso);
    var hoy = new Date();
    var cierreSoloFecha = new Date(cierre.getFullYear(), cierre.getMonth(), cierre.getDate());
    var hoySoloFecha = new Date(hoy.getFullYear(), hoy.getMonth(), hoy.getDate());
    var msPorDia = 24 * 60 * 60 * 1000;
    return Math.round((cierreSoloFecha - hoySoloFecha) / msPorDia);
  }

  function renderDiasParaCierre(dias) {
    if (dias == null) return '<span class="vacio">sin fecha</span>';
    var clase = dias <= 3 ? "badge-urgente" : "badge-normal";
    var etiqueta = dias < 0 ? "cerrada" : dias === 0 ? "cierra hoy" : dias + " día" + (dias === 1 ? "" : "s");
    return '<span class="badge ' + clase + '">' + etiqueta + "</span>";
  }

  // Monto y moneda se muestran juntos y SIN CONVERTIR (ver Candidata.cs):
  // "11.000 CLF" nunca se reduce a un número en pesos, porque eso haría que
  // UF o USD parezcan pesos chilenos. Ninguno de los dos llega poblado
  // todavía en F1 (requiere el detalle de sobrevivientes de F2).
  function renderMonto(candidata) {
    if (candidata.monto == null || !candidata.moneda) return '<span class="vacio">—</span>';
    var monto = Number(candidata.monto).toLocaleString("es-CL");
    return escaparHtml(monto) + " " + escaparHtml(candidata.moneda);
  }

  // Monto SIN redondear ni agrupar por miles (a diferencia de renderMonto,
  // que usa toLocaleString para la tabla compacta) — para el panel de
  // detalle expandible de la pestaña Revisión, donde el número exacto
  // importa más que la legibilidad rápida.
  function renderMontoExacto(candidata) {
    if (candidata.monto == null || !candidata.moneda) return '<span class="vacio">—</span>';
    return escaparHtml(String(candidata.monto)) + " " + escaparHtml(candidata.moneda);
  }

  // Lista de ítems UNSPSC con su descripción completa (candidata.categoria
  // en cada ítem, texto ya resuelto por la API con los niveles separados
  // por "/") — no solo el rubro ya resuelto por palabra, que es lo único
  // que ya se ve en la tabla compacta (renderRubro).
  function renderItemsUnspsc(candidata) {
    var items = candidata.items_unspsc || [];
    if (!items.length) return '<p class="vacio">Sin ítems UNSPSC (no enriquecido todavía).</p>';

    var filas = items.map(function (item) {
      return "<li>" +
        '<span class="codigo-producto">' + escaparHtml(String(item.codigo_producto || "—")) + "</span>" +
        " — " +
        (item.categoria ? escaparHtml(item.categoria) : '<span class="vacio">sin categoría resuelta</span>') +
        "</li>";
    }).join("");

    return "<ul class=\"lista-items-unspsc\">" + filas + "</ul>";
  }

  // Panel configurable (2026-09-22): en vez de una lista fija de campos en
  // código, config/panel-revision.json (editado por el usuario, publicado
  // en docs/ por Program.PublicarPanelRevisionConfig) decide qué se
  // muestra por default y qué queda detrás de "Mostrar más campos". Este
  // registro es el único lugar que mapea clave -> etiqueta + cómo
  // renderizarla — agregar un campo nuevo al inventario en el futuro es
  // agregar una entrada acá y a la config, no tocar renderTablaRevision ni
  // renderResultadoConsulta (ambos comparten este registro).
  var CAMPOS_PANEL = {
    descripcion: {
      etiqueta: "Descripción",
      render: function (c) { return c.descripcion ? escaparHtml(c.descripcion) : '<span class="vacio">—</span>'; },
    },
    fecha_cierre: {
      etiqueta: "Fecha de cierre",
      render: function (c) { return formatearFecha(c.fecha_cierre); },
    },
    organismo: {
      etiqueta: "Organismo",
      render: function (c) { return c.organismo ? escaparHtml(c.organismo) : '<span class="vacio">—</span>'; },
    },
    region: {
      etiqueta: "Región (cruda, tal cual la API)",
      render: function (c) { return c.region ? escaparHtml(c.region) : '<span class="vacio">—</span>'; },
    },
    comuna: {
      etiqueta: "Comuna",
      render: function (c) { return c.comuna ? escaparHtml(c.comuna) : '<span class="vacio">—</span>'; },
    },
    monto: {
      etiqueta: "Monto exacto",
      render: renderMontoExacto,
    },
    cantidad_reclamos: {
      etiqueta: "Cantidad de reclamos",
      render: function (c) {
        return c.cantidad_reclamos == null ? '<span class="vacio">—</span>' : escaparHtml(String(c.cantidad_reclamos));
      },
    },
    items_unspsc: {
      etiqueta: "Ítems UNSPSC",
      render: renderItemsUnspsc,
    },
    // sub_contratacion/tipo_pago (2026-09-22): códigos numéricos como
    // string ("1", "4", "0") SIN diccionario de traducción disponible
    // (ver DetalleLicitacionResponse.cs) — se muestran crudos, con la
    // etiqueta dejando claro que es un código sin traducir. Ocultos por
    // default en config/panel-revision.json a propósito: un código sin
    // contexto confunde más de lo que ayuda.
    sub_contratacion: {
      etiqueta: "Subcontratación (código, sin traducir)",
      render: function (c) { return c.sub_contratacion != null ? escaparHtml(c.sub_contratacion) : '<span class="vacio">—</span>'; },
    },
    prohibicion_contratacion: {
      etiqueta: "Prohibición de contratación",
      render: function (c) {
        return c.prohibicion_contratacion ? escaparHtml(c.prohibicion_contratacion) : '<span class="vacio">—</span>';
      },
    },
    tipo_pago: {
      etiqueta: "Tipo de pago (código, sin traducir)",
      render: function (c) { return c.tipo_pago != null ? escaparHtml(c.tipo_pago) : '<span class="vacio">—</span>'; },
    },
  };

  // Fallback si config/panel-revision.json todavía no se publicó (antes de
  // la primera corrida tras este cambio) o la lectura falla — nunca un
  // panel roto por un 404, mismo contenido que el archivo inicial.
  var PANEL_CONFIG_DEFAULT = {
    campos_visibles: ["descripcion", "fecha_cierre", "organismo", "region", "monto", "cantidad_reclamos"],
    campos_ocultos_por_default: ["items_unspsc", "comuna", "sub_contratacion", "prohibicion_contratacion", "tipo_pago"],
  };

  var panelRevisionConfig = PANEL_CONFIG_DEFAULT;

  function cargarPanelRevisionConfig() {
    return fetch("panel-revision.json")
      .then(function (respuesta) {
        if (!respuesta.ok) throw new Error("panel-revision.json no disponible (HTTP " + respuesta.status + ")");
        return respuesta.json();
      })
      .then(function (config) { panelRevisionConfig = config; })
      .catch(function () { panelRevisionConfig = PANEL_CONFIG_DEFAULT; });
  }

  function renderBloqueCampos(candidata, claves) {
    return (claves || []).map(function (clave) {
      var campo = CAMPOS_PANEL[clave];
      if (!campo) {
        // Un campo referenciado en config/panel-revision.json que no
        // existe en el registro (typo del usuario al editar, o un campo
        // retirado del código) se ignora — nunca rompe el panel.
        console.warn('config/panel-revision.json referencia un campo desconocido: "' + clave + '"');
        return "";
      }
      return "<div><strong>" + escaparHtml(campo.etiqueta) + ":</strong> " + campo.render(candidata) + "</div>";
    }).join("");
  }

  // Panel de detalle expandible (2026-09-21/22): datos que ya vienen en
  // docs/data.json pero que la tabla compacta no muestra. Compartido entre
  // la pestaña Revisión (renderTablaRevision) y la pestaña Consulta
  // (renderResultadoConsulta) — cualquier campo nuevo que se agregue al
  // registro CAMPOS_PANEL aparece automáticamente en ambos.
  function renderCamposConfigurados(candidata) {
    var config = panelRevisionConfig || PANEL_CONFIG_DEFAULT;
    var visibles = config.campos_visibles || PANEL_CONFIG_DEFAULT.campos_visibles;
    var ocultos = config.campos_ocultos_por_default || PANEL_CONFIG_DEFAULT.campos_ocultos_por_default;
    var idOcultos = "campos-ocultos-" + Math.random().toString(36).slice(2);

    return '<div class="panel-detalle">' +
      renderBloqueCampos(candidata, visibles) +
      (ocultos.length
        ? '<button class="boton-detalle" data-toggle-ocultos="' + idOcultos + '" aria-expanded="false">▸ Mostrar más campos</button>' +
          '<div id="' + idOcultos + '" class="bloque-campos-ocultos" hidden>' + renderBloqueCampos(candidata, ocultos) + "</div>"
        : "") +
      "</div>";
  }

  // Activa el toggle "Mostrar más campos" para todos los paneles dentro de
  // un contenedor — se llama después de escribir innerHTML, tanto en
  // Revisión (una vez por fila) como en Consulta (una vez por resultado).
  function activarTogglesOcultos(contenedor) {
    contenedor.querySelectorAll("button[data-toggle-ocultos]").forEach(function (boton) {
      boton.addEventListener("click", function () {
        var bloqueOcultos = document.getElementById(boton.getAttribute("data-toggle-ocultos"));
        var expandido = boton.getAttribute("aria-expanded") === "true";
        bloqueOcultos.hidden = expandido;
        boton.setAttribute("aria-expanded", String(!expandido));
        boton.textContent = (expandido ? "▸" : "▾") + " Mostrar más campos";
      });
    });
  }

  // Adjudicacion (2026-09-21/22): metadata del acta a nivel de licitación
  // (fecha/número/oferentes/link) — NUNCA trae el ganador, verificado
  // contra 1000813-15-LE26. El ganador vive por ítem
  // (detalle.items.listado[].adjudicacion: rut_proveedor/nombre_proveedor/
  // monto_unitario, ver renderGanadores) — hallazgo del 2026-09-22 que
  // corrige la conclusión anterior ("no sirve para ver ganadores"): sí se
  // puede, sin scraping, solo estaba en otro nivel del JSON.
  function renderPanelAdjudicacion(detalle) {
    var adj = detalle && detalle.adjudicacion;
    if (!adj) return '<p class="vacio">Sin información de adjudicación.</p>';

    return '<ul class="lista-items-unspsc">' +
      "<li><strong>Fecha:</strong> " + (adj.fecha ? formatearFecha(adj.fecha) : '<span class="vacio">—</span>') + "</li>" +
      "<li><strong>Número de acta:</strong> " + (adj.numero ? escaparHtml(adj.numero) : '<span class="vacio">—</span>') + "</li>" +
      "<li><strong>Oferentes:</strong> " + (adj.numero_oferentes != null ? escaparHtml(String(adj.numero_oferentes)) : '<span class="vacio">—</span>') + "</li>" +
      (adj.url_acta
        ? '<li><a href="' + escaparHtml(adj.url_acta) + '" target="_blank" rel="noopener">Ver acta de adjudicación (Mercado Público) ↗</a></li>'
        : "") +
      "</ul>";
  }

  // Ganador (2026-09-22): por ítem, no a nivel de la licitación completa
  // (ver renderPanelAdjudicacion arriba) — la mayoría de las licitaciones
  // tienen un solo ítem, pero se itera por si acaso más de uno quedó
  // adjudicado a proveedores distintos. String vacío mientras no haya
  // ningún ítem adjudicado, para que el llamador decida si mostrar la
  // sección o no.
  function renderGanadores(detalle) {
    var items = (detalle.items && detalle.items.listado) || [];
    var conGanador = items.filter(function (item) { return item.adjudicacion; });
    if (!conGanador.length) return "";

    var filas = conGanador.map(function (item) {
      var g = item.adjudicacion;
      var monto = g.monto_unitario != null
        ? " — " + escaparHtml(String(g.monto_unitario)) + (detalle.moneda ? " " + escaparHtml(detalle.moneda) : "")
        : "";
      return "<li>" +
        (g.nombre_proveedor ? escaparHtml(g.nombre_proveedor) : '<span class="vacio">proveedor sin nombre</span>') +
        (g.rut_proveedor ? " (RUT " + escaparHtml(g.rut_proveedor) + ")" : "") +
        monto +
        "</li>";
    }).join("");

    return '<div><strong>Ganador:</strong><ul class="lista-items-unspsc">' + filas + "</ul></div>";
  }

  // Adapta el shape crudo de ResultadoConsulta (snake_case, ver
  // Modelos/ResultadoConsulta.cs y DetalleLicitacionResponse.cs) al shape
  // que ya espera CAMPOS_PANEL (mismos campos que docs/data.json) — evita
  // duplicar los renders entre Revisión y Consulta.
  function renderResultadoConsulta(resultado, codigoIngresado) {
    var contenedor = document.getElementById("resultado-consulta");

    if (!resultado || !resultado.encontrado) {
      contenedor.innerHTML = '<p class="vacio">No se encontró información para el código '
        + escaparHtml(codigoIngresado) + ".</p>";
      return;
    }

    var d = resultado.detalle;
    var candidataLike = {
      items_unspsc: (d.items && d.items.listado || []).map(function (item) {
        return { codigo_producto: item.codigo_producto, categoria: item.categoria };
      }),
      region: d.comprador && d.comprador.region_unidad,
      organismo: d.comprador && d.comprador.nombre_organismo,
      comuna: d.comprador && d.comprador.comuna_unidad,
      moneda: d.moneda,
      monto: d.monto_estimado,
      descripcion: d.descripcion,
      fecha_cierre: d.fechas && d.fechas.fecha_cierre,
      cantidad_reclamos: d.cantidad_reclamos,
      prohibicion_contratacion: d.prohibicion_contratacion,
      tipo_pago: d.tipo_pago,
      sub_contratacion: d.sub_contratacion,
    };

    contenedor.innerHTML =
      '<div class="panel-detalle">' +
      "<div><strong>Código:</strong> " + escaparHtml(resultado.codigo_externo) + "</div>" +
      "<div><strong>Código de estado:</strong> " + escaparHtml(String(d.codigo_estado)) + "</div>" +
      "</div>" +
      renderCamposConfigurados(candidataLike) +
      '<div class="panel-detalle">' +
      "<div><strong>Adjudicación:</strong>" + renderPanelAdjudicacion(d) + "</div>" +
      renderGanadores(d) +
      '<div class="vacio">Consultado el ' + new Date(resultado.consultado_en).toLocaleString("es-CL") + "</div>" +
      "</div>";

    activarTogglesOcultos(contenedor);
  }

  // Flujo: (1) leer data/consultas/{codigo}.json — si existe, mostrarlo
  // directo, sin disparar nada. (2) Si no existe, disparar workflow_dispatch
  // con consultar_licitacion=codigo, mostrar "Consultando…" y hacer polling
  // hasta que el run termine. (3) Reintentar la lectura y renderizar. Nunca
  // falla en silencio: cualquier error de red/API se muestra tal cual en el
  // panel de resultado.
  function renderConsultaActiva() {
    var contenedor = document.getElementById("tabla-candidatas");
    contenedor.innerHTML =
      '<div class="panel-consulta">' +
      '<div class="fila-consulta">' +
      '<input type="text" id="input-consulta" placeholder="Código de licitación (ej. 734-50-LE26)" />' +
      '<button id="btn-consultar" class="boton-accion boton-accion-primaria">Consultar</button>' +
      "</div>" +
      '<div id="estado-consulta" class="vacio"></div>' +
      '<div id="resultado-consulta"></div>' +
      "</div>";

    var input = document.getElementById("input-consulta");
    var boton = document.getElementById("btn-consultar");
    var estado = document.getElementById("estado-consulta");
    var resultado = document.getElementById("resultado-consulta");

    function habilitar() {
      boton.disabled = false;
      input.disabled = false;
    }

    function mostrarError(error) {
      estado.textContent = "";
      resultado.innerHTML = '<p class="vacio">' + escaparHtml(error.message) + "</p>";
      habilitar();
    }

    boton.addEventListener("click", function () {
      var codigo = input.value.trim();
      if (!codigo) return;

      boton.disabled = true;
      input.disabled = true;
      resultado.innerHTML = "";
      estado.textContent = "Buscando " + codigo + "…";

      leerConsulta(codigo)
        .then(function (encontrado) {
          if (encontrado) {
            estado.textContent = "";
            renderResultadoConsulta(encontrado, codigo);
            habilitar();
            return;
          }

          var token = githubToken() || pedirToken();
          if (!token) {
            mostrarError(new Error("Sin token, no se pudo disparar la consulta."));
            return;
          }

          var desde = new Date();
          estado.textContent = "Consultando… (puede tardar 1-2 minutos por la cola de Actions)";

          dispararConsulta(codigo)
            .then(function () { return esperarRunDeConsulta(token, desde); })
            .then(function () { return leerConsulta(codigo); })
            .then(function (encontradoTrasCorrida) {
              estado.textContent = "";
              renderResultadoConsulta(encontradoTrasCorrida, codigo);
              habilitar();
            })
            .catch(mostrarError);
        })
        .catch(mostrarError);
    });

    input.addEventListener("keydown", function (evento) {
      if (evento.key === "Enter") boton.click();
    });
  }

  function renderTabla(candidatas) {
    var contenedor = document.getElementById("tabla-candidatas");

    if (!candidatas.length) {
      contenedor.innerHTML = '<p class="vacio">No hay registros en esta lista.</p>';
      return;
    }

    var filas = candidatas.map(function (c) {
      return "<tr>" +
        "<td>" + renderCodigo(c) + "</td>" +
        "<td>" + escaparHtml(c.nombre) + "</td>" +
        "<td>" + renderTipo(c) + "</td>" +
        "<td>" + renderRubro(c) + "</td>" +
        "<td>" + renderUnspscEstado(c) + "</td>" +
        "<td>" + renderRegion(c) + "</td>" +
        "<td>" + renderMonto(c) + "</td>" +
        "<td>" + formatearFecha(c.fecha_cierre) + "</td>" +
        "<td>" + renderDiasParaCierre(diasParaCierre(c.fecha_cierre)) + "</td>" +
        "<td>" + escaparHtml(c.estado_flujo) + "</td>" +
        "<td>" + escaparHtml(c.origen) + "</td>" +
        "<td>" + formatearFecha(c.fecha_lote) + "</td>" +
        "</tr>";
    }).join("");

    contenedor.innerHTML =
      "<table>" +
      "<thead><tr>" +
      "<th>Código</th><th>Nombre</th><th>Tipo</th><th>Rubro</th><th>UNSPSC</th><th>Región</th><th>Monto</th>" +
      "<th>Cierre</th><th>Plazo</th><th>Estado</th><th>Origen</th><th>Lote</th>" +
      "</tr></thead>" +
      "<tbody>" + filas + "</tbody>" +
      "</table>";

    contenedor.querySelectorAll("[data-copiar]").forEach(function (span) {
      span.addEventListener("click", function () {
        var codigo = span.getAttribute("data-copiar");
        var textoOriginal = span.textContent;
        navigator.clipboard.writeText(codigo).then(function () {
          span.textContent = "copiado";
          setTimeout(function () {
            span.textContent = textoOriginal;
          }, 1000);
        });
      });
    });
  }

  // Pestaña "Revisión": junta unspsc_estado=revision_manual y
  // estado_flujo=revision_ambigua/revision_degradada de Secundarias (ver
  // razonRevision) — hoy invisibles salvo bajando el CSV completo. Cada
  // fila trae dos botones
  // que escriben un override vía la API de GitHub (ver guardarOverride);
  // el resultado real (mover la candidata de lista) lo aplica la próxima
  // corrida nocturna del pipeline, nunca esta página.
  function renderTablaRevision(candidatas) {
    var contenedor = document.getElementById("tabla-candidatas");

    if (!candidatas.length) {
      contenedor.innerHTML = '<p class="vacio">Todo al día — no hay nada pendiente de revisión.</p>';
      return;
    }

    var filas = candidatas.map(function (c) {
      return '<tr data-codigo="' + escaparHtml(c.codigo) + '">' +
        "<td>" +
          '<button class="boton-detalle" data-toggle-detalle aria-expanded="false">▸ Ver detalle</button>' +
        "</td>" +
        "<td>" + renderCodigo(c) + "</td>" +
        "<td>" + escaparHtml(c.nombre) + "</td>" +
        "<td>" + renderRubro(c) + "</td>" +
        "<td>" + razonRevision(c) + "</td>" +
        "<td>" + renderRegion(c) + "</td>" +
        '<td class="fila-acciones">' +
          '<button class="boton-accion boton-accion-primaria" data-accion="prioritarias">Mover a Prioritarias</button>' +
          '<button class="boton-accion" data-accion="secundarias">Confirmar en Secundarias</button>' +
        "</td>" +
        "</tr>" +
        '<tr class="fila-detalle" hidden><td colspan="7">' + renderCamposConfigurados(c) + "</td></tr>";
    }).join("");

    contenedor.innerHTML =
      "<table>" +
      "<thead><tr>" +
      "<th></th><th>Código</th><th>Nombre</th><th>Rubro</th><th>Motivo</th><th>Región</th><th>Acciones</th>" +
      "</tr></thead>" +
      "<tbody>" + filas + "</tbody>" +
      "</table>";

    contenedor.querySelectorAll("button[data-toggle-detalle]").forEach(function (boton) {
      boton.addEventListener("click", function () {
        var filaDetalle = boton.closest("tr").nextElementSibling;
        var expandido = boton.getAttribute("aria-expanded") === "true";
        filaDetalle.hidden = expandido;
        boton.setAttribute("aria-expanded", String(!expandido));
        boton.textContent = (expandido ? "▸" : "▾") + " Ver detalle";
      });
    });
    activarTogglesOcultos(contenedor);

    contenedor.querySelectorAll("tr[data-codigo]").forEach(function (fila) {
      var codigo = fila.getAttribute("data-codigo");
      var botones = fila.querySelectorAll("button[data-accion]");

      botones.forEach(function (boton) {
        boton.addEventListener("click", function () {
          var destino = boton.getAttribute("data-accion");
          var textoOriginal = boton.textContent;
          botones.forEach(function (b) { b.disabled = true; });
          boton.textContent = "Guardando…";

          guardarOverride(codigo, destino)
            .then(function () {
              fila.querySelector(".fila-acciones").textContent =
                "Guardado — se aplica en la próxima corrida nocturna.";
            })
            .catch(function (error) {
              botones.forEach(function (b) { b.disabled = false; });
              boton.textContent = textoOriginal;
              alert("No se pudo guardar la decisión: " + error.message);
            });
        });
      });
    });
  }

  // Exportación a CSV generada enteramente en el cliente, sin servidor.
  // Comillas RFC4180 correctas en los campos de texto — el problema real que
  // motivó esto fue justo lo contrario: el CSV que exporta Mercado Público
  // usa ';' sin encomillar campos que contienen ';' embebido y desalinea
  // columnas en silencio (ver JsonStore.cs, caso 85-34-LP26).
  function csvEscapar(valor) {
    var texto = valor == null ? "" : String(valor);
    if (/["\n,]/.test(texto)) {
      return '"' + texto.replace(/"/g, '""') + '"';
    }
    return texto;
  }

  function candidatasACsv(candidatas) {
    var columnas = [
      "codigo", "nombre", "tipo", "tipo_privado", "rubro_match", "termino_match",
      "unspsc_estado", "region", "moneda", "monto", "fecha_cierre", "dias_para_cierre",
      "estado_flujo", "origen", "fecha_lote",
    ];
    var filas = [columnas.join(",")];
    candidatas.forEach(function (c) {
      // dias_para_cierre no viene en data.json (ver diasParaCierre): se
      // recalcula aquí, en el momento exacto del click, para que el CSV
      // nunca traiga un plazo desactualizado.
      var fila = Object.assign({}, c, { dias_para_cierre: diasParaCierre(c.fecha_cierre) });
      filas.push(columnas.map(function (col) { return csvEscapar(fila[col]); }).join(","));
    });
    return filas.join("\r\n");
  }

  function descargarCsv(candidatas, nombreTab) {
    // BOM UTF-8 para que Excel abra bien las tildes.
    var blob = new Blob(["﻿" + candidatasACsv(candidatas)], { type: "text/csv;charset=utf-8;" });
    var url = URL.createObjectURL(blob);
    var enlace = document.createElement("a");
    enlace.href = url;
    enlace.download = nombreTab + "-" + new Date().toISOString().slice(0, 10) + ".csv";
    document.body.appendChild(enlace);
    enlace.click();
    document.body.removeChild(enlace);
    URL.revokeObjectURL(url);
  }

  function renderGrafico(serie) {
    var contenedor = document.getElementById("grafico-serie");

    if (!serie.length) {
      contenedor.innerHTML = '<p class="vacio">Todavía no hay serie histórica.</p>';
      return;
    }

    var ancho = 900;
    var alto = 220;
    var margen = { arriba: 16, abajo: 28, izquierda: 44, derecha: 16 };
    var anchoUtil = ancho - margen.izquierda - margen.derecha;
    var altoUtil = alto - margen.arriba - margen.abajo;

    var tasaMaxima = Math.max.apply(null, serie.map(function (p) { return p.tasa; }).concat([0.001]));
    var escalaY = function (tasa) { return margen.arriba + altoUtil - (tasa / tasaMaxima) * altoUtil; };
    var escalaX = function (i) {
      return serie.length === 1
        ? margen.izquierda + anchoUtil / 2
        : margen.izquierda + (i / (serie.length - 1)) * anchoUtil;
    };

    var puntos = serie.map(function (p, i) { return escalaX(i) + "," + escalaY(p.tasa); }).join(" ");

    var circulos = serie.map(function (p, i) {
      return '<circle class="grafico-punto" cx="' + escalaX(i) + '" cy="' + escalaY(p.tasa) + '" r="3">' +
        "<title>" + formatearFecha(p.fecha) + ": " + (p.tasa * 100).toFixed(2) + "% (" + p.prioritarias + "/" + p.total + ")</title>" +
        "</circle>";
    }).join("");

    var etiquetasX = serie.map(function (p, i) {
      if (serie.length > 8 && i % Math.ceil(serie.length / 8) !== 0) return "";
      return '<text x="' + escalaX(i) + '" y="' + (alto - 6) + '" text-anchor="middle">' + formatearFecha(p.fecha) + "</text>";
    }).join("");

    contenedor.innerHTML =
      '<svg class="grafico-serie" viewBox="0 0 ' + ancho + " " + alto + '" role="img" aria-label="Tasa de rubro prioritario por día">' +
      '<line class="grafico-eje" x1="' + margen.izquierda + '" y1="' + margen.arriba + '" x2="' + margen.izquierda + '" y2="' + (alto - margen.abajo) + '" />' +
      '<line class="grafico-eje" x1="' + margen.izquierda + '" y1="' + (alto - margen.abajo) + '" x2="' + (ancho - margen.derecha) + '" y2="' + (alto - margen.abajo) + '" />' +
      '<polyline class="grafico-linea" points="' + puntos + '" />' +
      circulos +
      etiquetasX +
      "</svg>";
  }

  // panel-revision.json se carga en paralelo, no bloquea el render de
  // data.json — si tarda o falla, cargarPanelRevisionConfig ya deja
  // panelRevisionConfig en PANEL_CONFIG_DEFAULT (ver su propio catch).
  cargarPanelRevisionConfig();

  fetch("data.json")
    .then(function (respuesta) { return respuesta.json(); })
    .then(function (datos) {
      var datasets = {
        prioritarias: datos.prioritarias || [],
        secundarias: datos.secundarias || [],
        tramo_bajo: datos.tramo_bajo || [],
      };
      var tabActiva = "prioritarias";

      document.getElementById("generado-en").textContent = datos.generado_en
        ? "Última actualización: " + new Date(datos.generado_en).toLocaleString("es-CL")
        : "Todavía sin corridas.";

      var botonCsv = document.getElementById("btn-descargar-csv");
      var panelGrafico = document.getElementById("panel-grafico");
      var notaRevision = document.getElementById("nota-revision");

      // "Revisión" no es un dataset propio de data.json: se computa al
      // vuelo filtrando Secundarias (ver razonRevision) y cruzando contra
      // data/overrides.json (vía la API de GitHub, async) — una fila con
      // override ya tiene una decisión humana encima, así que desaparece de
      // la cola aunque el pipeline todavía no haya corrido para aplicarla.
      function renderRevisionActiva() {
        var contenedor = document.getElementById("tabla-candidatas");
        contenedor.innerHTML = '<p class="vacio">Cargando…</p>';

        var enCola = datasets.secundarias.filter(function (c) {
          return c.unspsc_estado === "revision_manual"
            || c.estado_flujo === "revision_ambigua"
            || c.estado_flujo === "revision_degradada";
        });

        if (!enCola.length) {
          renderTablaRevision([]);
          return;
        }

        leerOverrides()
          .then(function (actual) {
            var pendientes = enCola.filter(function (c) {
              return !Object.prototype.hasOwnProperty.call(actual.overrides, c.codigo);
            });
            renderTablaRevision(pendientes);
          })
          .catch(function (error) {
            // Sin poder leer overrides (sin token en un repo privado, error
            // de red): se muestra la cola completa sin filtrar — nunca se
            // oculta trabajo pendiente por un fallo de lectura.
            console.error(error);
            renderTablaRevision(enCola);
          });
      }

      function renderTabActiva() {
        notaRevision.hidden = tabActiva !== "revision";

        if (tabActiva === "revision") {
          panelGrafico.hidden = true;
          botonCsv.hidden = true;
          renderRevisionActiva();
          return;
        }

        // "Consulta" tampoco es un dataset de data.json (ver Consulta,
        // 2026-09-22): no hay CSV ni gráfico que le correspondan, y su
        // contenido no depende de datasets — se regenera vacía cada vez
        // que se entra a la pestaña (misma decisión que Revisión, que
        // tampoco preserva estado entre pestañas).
        if (tabActiva === "consulta") {
          panelGrafico.hidden = true;
          botonCsv.hidden = true;
          renderConsultaActiva();
          return;
        }

        var candidatas = datasets[tabActiva];
        renderTabla(candidatas);

        // El gráfico de tasa de rubro es específico de Prioritarias (Lista
        // A) — la vista por defecto no cambia respecto al diseño original.
        panelGrafico.hidden = tabActiva !== "prioritarias";

        botonCsv.hidden = candidatas.length === 0;
        botonCsv.onclick = function () { descargarCsv(candidatas, tabActiva); };
      }

      document.querySelectorAll(".tab").forEach(function (boton) {
        boton.addEventListener("click", function () {
          tabActiva = boton.getAttribute("data-tab");
          document.querySelectorAll(".tab").forEach(function (b) {
            var activo = b === boton;
            b.classList.toggle("tab-activa", activo);
            b.setAttribute("aria-selected", activo ? "true" : "false");
          });
          renderTabActiva();
        });
      });

      renderTabActiva();
      renderGrafico(datos.serie_tasa_rubro || []);
    })
    .catch(function (error) {
      document.getElementById("generado-en").textContent = "No se pudo cargar data.json.";
      console.error(error);
    });
})();
