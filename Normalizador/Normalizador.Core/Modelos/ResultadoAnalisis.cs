namespace Normalizador.Core.Modelos;

public class ResultadoAnalisis
{
    public List<string> Errores { get; set; } = new();
    public List<Tabla> Tablas { get; set; } = new();
    public List<ResultadoRegla> Resultados { get; set; } = new();

    public bool ScriptValido => Errores.Count == 0;
    public bool PuedeContinuar => ScriptValido && !Resultados.Any(r => r.Estado == EstadoRegla.NoCumple);
}