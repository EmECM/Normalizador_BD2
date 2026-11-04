using Normalizador.Core.Lectura;
using Normalizador.Core.Modelos;
using Normalizador.Core.Reglas;

namespace Normalizador.Core.Servicios;

public class AnalizadorPrerrequisitos
{
    private readonly List<IReglaTabla> _reglas = new()
    {
        new ReglaClavePrimaria(),
        new ReglaColumnasDuplicadas(),
        new ReglaGruposRepetitivos(),
        new ReglaAtomicidad(),
        new ReglaClavesForaneas()
    };

    public ResultadoAnalisis Analizar(string script)
    {
        var resultado = new ResultadoAnalisis();

        var (tablas, errores) = new LectorSql().Leer(script);
        resultado.Errores.AddRange(errores);
        resultado.Tablas.AddRange(tablas);

        if (resultado.Errores.Count > 0) return resultado;

        if (tablas.Count == 0)
        {
            resultado.Errores.Add("No se encontró ningún CREATE TABLE en el archivo.");
            return resultado;
        }

        foreach (var tabla in tablas)
            foreach (var regla in _reglas)
                resultado.Resultados.Add(regla.Evaluar(tabla, tablas));

        return resultado;
    }
}