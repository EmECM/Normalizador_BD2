namespace Normalizador.Core.Modelos;

public class Resultado1FN
{
    public List<ResultadoRegla> EvaluacionesIniciales { get; set; } = new();
    public List<CambioAplicado> Cambios { get; set; } = new();
    public List<Tabla> Tablas1FN { get; set; } = new();
    public List<ResultadoRegla> EvaluacionesFinales { get; set; } = new();

    public bool CumpleInicialmente => EvaluacionesIniciales.All(r => r.Estado == EstadoRegla.Cumple);
    public bool HuboCambios => Cambios.Count > 0;
    public bool Cumple1FN => EvaluacionesFinales.All(r => r.Estado == EstadoRegla.Cumple);
}
