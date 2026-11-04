namespace Normalizador.Core.Modelos;

public class ClaveForanea
{
    public List<string> Columnas { get; set; } = new();
    public string TablaReferenciada { get; set; } = "";
    public List<string> ColumnasReferenciadas { get; set; } = new();
}