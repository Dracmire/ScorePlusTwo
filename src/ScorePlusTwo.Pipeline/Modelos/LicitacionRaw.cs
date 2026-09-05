namespace ScorePlusTwo.Pipeline.Modelos;

// CodigoEstado es int plano, no enum: la API real trae códigos no documentados
// en la especificación (ej. 15), y el modelo no debe romperse por eso.
// FechaCierre es nullable: el fixture real trae al menos un registro con
// FechaCierre null (estado Publicada igual), y el modelo no debe crashear
// con eso — consistente con tratar la fuente como beta/inconsistente.
public sealed record LicitacionRaw(
    string CodigoExterno,
    string Nombre,
    int CodigoEstado,
    DateTime? FechaCierre);
