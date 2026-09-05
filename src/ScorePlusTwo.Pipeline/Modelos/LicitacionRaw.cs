namespace ScorePlusTwo.Pipeline.Modelos;

// CodigoEstado es int plano, no enum: la API real trae códigos no documentados
// en la especificación (ej. 15), y el modelo no debe romperse por eso.
// FechaCierre es nullable: el fixture real trae al menos un registro con
// FechaCierre null (estado Publicada igual), y el modelo no debe crashear
// con eso — consistente con tratar la fuente como beta/inconsistente.
//
// FechaCierre: la API oficial de ChileCompra es la ÚNICA fuente de verdad
// para esta fecha, siempre. Se detectó en producción una discrepancia de 7
// días entre esta API y el MCP de LicitaLab para 4956-74-LE26 (LicitaLab
// reportó cierre 07-09, la API oficial y la ficha real dicen 14-09 15:01) —
// de haber confiado en la otra fuente se habría descartado por error una
// licitación con plazo vigente. La API entrega esta fecha en hora local de
// Chile SIN sufijo de zona; NUNCA llamar ToUniversalTime()/ToLocalTime() ni
// agregarle "Z" al serializarla — eso la reinterpretaría como UTC y la
// corriría varias horas. Si en el futuro se incorpora otra fuente para
// enriquecer candidatas, esta fecha nunca se sobrescribe con la de esa otra
// fuente: la API oficial manda siempre.
public sealed record LicitacionRaw(
    string CodigoExterno,
    string Nombre,
    int CodigoEstado,
    DateTime? FechaCierre);
