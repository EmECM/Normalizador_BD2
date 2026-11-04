using Normalizador.Core.Modelos;

namespace Normalizador.Core.Reglas;

public interface IReglaTabla
{
    string Nombre { get; }
    ResultadoRegla Evaluar(Tabla tabla, IReadOnlyList<Tabla> todasLasTablas);
}