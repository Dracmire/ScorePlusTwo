(function () {
  "use strict";

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
      "<th>Código</th><th>Nombre</th><th>Tipo</th><th>Rubro</th><th>Monto</th>" +
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
      "moneda", "monto", "fecha_cierre", "dias_para_cierre", "estado_flujo",
      "origen", "fecha_lote",
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

      function renderTabActiva() {
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
