namespace Normalizador.Core.Modelos;

public enum EstadoRegla
{
    Cumple,
    Advertencia,
    NoCumple
}

public class ResultadoRegla
{
    public string Tabla { get; set; } = "";
    public string Regla { get; set; } = "";
    public EstadoRegla Estado { get; set; }
    public string Detalle { get; set; } = "";

    public static ResultadoRegla De(Tabla tabla, string regla, EstadoRegla estado, string detalle) =>
        new() { Tabla = tabla.Nombre, Regla = regla, Estado = estado, Detalle = detalle };
}