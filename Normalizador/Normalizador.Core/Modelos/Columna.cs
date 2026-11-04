namespace Normalizador.Core.Modelos;

public class Columna
{
    public string Nombre { get; set; } = "";
    public string TipoDato { get; set; } = "";
    public bool PermiteNulos { get; set; } = true;
    public bool EsPrimaryKey { get; set; }
    public bool EsUnica { get; set; }
    public bool EsIdentity { get; set; }
}