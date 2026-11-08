namespace Normalizador.Core.Modelos;

// Una modificación concreta que el corrector hizo sobre el script
public class CambioAplicado
{
    public string Tabla { get; set; } = "";
    public string Regla { get; set; } = "";
    public string Descripcion { get; set; } = "";
}

public class ResultadoCorreccion
{
    public string ScriptOriginal { get; set; } = "";
    public string ScriptCorregido { get; set; } = "";
    public List<CambioAplicado> Cambios { get; set; } = new();

    // Informe de prerrequisitos volviendo a analizar el script ya corregido
    public ResultadoAnalisis? AnalisisCorregido { get; set; }

    public bool HuboCambios => Cambios.Count > 0;
}
