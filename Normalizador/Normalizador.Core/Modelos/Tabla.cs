namespace Normalizador.Core.Modelos;

public class Tabla
{
    public string Esquema { get; set; } = "dbo";
    public string Nombre { get; set; } = "";
    public List<Columna> Columnas { get; set; } = new();
    public List<ClaveForanea> ClavesForaneas { get; set; } = new();
    public List<Dictionary<string, string?>> Filas { get; set; } = new();

    public List<Columna> ClavePrimaria => Columnas.Where(c => c.EsPrimaryKey).ToList();
    public bool TienePrimaryKey => Columnas.Any(c => c.EsPrimaryKey);
}
