namespace Normalizador.Core.Modelos;

public class ResultadoFormaNormal
{
    public string NombreFormaNormal { get; set; } = "";
    public List<ResultadoRegla> EvaluacionesIniciales { get; set; } = new();
    public List<CambioAplicado> Cambios { get; set; } = new();
    public List<Tabla> TablasNormalizadas { get; set; } = new();
    public List<ResultadoRegla> EvaluacionesFinales { get; set; } = new();

    public bool CumpleInicialmente => EvaluacionesIniciales.All(r => r.Estado == EstadoRegla.Cumple);
    public bool HuboCambios => Cambios.Count > 0;
    public bool CumpleFinal => EvaluacionesFinales.All(r => r.Estado == EstadoRegla.Cumple);
}
