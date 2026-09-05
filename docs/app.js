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

  function renderDiasParaCierre(dias) {
    if (dias == null) return '<span class="vacio">sin fecha</span>';
    var clase = dias <= 3 ? "badge-urgente" : "badge-normal";
    var etiqueta = dias < 0 ? "cerrada" : dias === 0 ? "cierra hoy" : dias + " día" + (dias === 1 ? "" : "s");
    return '<span class="badge ' + clase + '">' + etiqueta + "</span>";
  }

  function renderTabla(candidatas) {
    var contenedor = document.getElementById("tabla-candidatas");

    if (!candidatas.length) {
      contenedor.innerHTML = '<p class="vacio">No hay candidatas vigentes.</p>';
      return;
    }

    var filas = candidatas.map(function (c) {
      return "<tr>" +
        "<td>" + renderCodigo(c) + "</td>" +
        "<td>" + escaparHtml(c.nombre) + "</td>" +
        "<td>" + escaparHtml(c.tipo) + "</td>" +
        "<td>" + escaparHtml(c.rubro_match) + " · " + escaparHtml(c.termino_match) + "</td>" +
        "<td>" + formatearFecha(c.fecha_cierre) + "</td>" +
        "<td>" + renderDiasParaCierre(c.dias_para_cierre) + "</td>" +
        "<td>" + escaparHtml(c.estado_flujo) + "</td>" +
        "</tr>";
    }).join("");

    contenedor.innerHTML =
      "<table>" +
      "<thead><tr>" +
      "<th>Código</th><th>Nombre</th><th>Tipo</th><th>Rubro</th>" +
      "<th>Cierre</th><th>Plazo</th><th>Estado</th>" +
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
        "<title>" + formatearFecha(p.fecha) + ": " + (p.tasa * 100).toFixed(2) + "% (" + p.candidatas + "/" + p.total + ")</title>" +
        "</circle>";
    }).join("");

    var etiquetasX = serie.map(function (p, i) {
      if (serie.length > 8 && i % Math.ceil(serie.length / 8) !== 0) return "";
      return '<text x="' + escalaX(i) + '" y="' + (alto - 6) + '" text-anchor="middle">' + formatearFecha(p.fecha) + "</text>";
    }).join("");

    contenedor.innerHTML =
      '<svg class="grafico-serie" viewBox="0 0 ' + ancho + " " + alto + '" role="img" aria-label="Tasa de rubro por día">' +
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
      document.getElementById("generado-en").textContent = datos.generado_en
        ? "Última actualización: " + new Date(datos.generado_en).toLocaleString("es-CL")
        : "Todavía sin corridas.";
      renderTabla(datos.candidatas || []);
      renderGrafico(datos.serie_tasa_rubro || []);
    })
    .catch(function (error) {
      document.getElementById("generado-en").textContent = "No se pudo cargar data.json.";
      console.error(error);
    });
})();
