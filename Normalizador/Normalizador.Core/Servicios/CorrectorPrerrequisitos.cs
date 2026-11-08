using Microsoft.SqlServer.TransactSql.ScriptDom;
using Normalizador.Core.Correccion;
using Normalizador.Core.Lectura;
using Normalizador.Core.Modelos;

namespace Normalizador.Core.Servicios;

// Modifica el script para que cumpla los prerrequisitos que marcaron "No cumple":
//   - sin clave primaria        -> agrega una clave sustituta (o usa la columna IDENTITY existente)
//   - columnas duplicadas       -> elimina las repeticiones
//   - claves foráneas inválidas -> las quita
// Las "Advertencia" (grupos repetitivos, multivalores) no se tocan: son parte del análisis de 1FN.
public class CorrectorPrerrequisitos
{
    // Una corrección puede dejar al descubierto otra (p. ej. al quitar una columna duplicada que traía la PK),
    // así que se repite hasta que todo cumpla o ya no haya nada más que arreglar.
    private const int MaxPasadas = 3;

    private readonly AnalizadorPrerrequisitos _analizador = new();

    public ResultadoCorreccion Corregir(string script)
    {
        var resultado = new ResultadoCorreccion { ScriptOriginal = script, ScriptCorregido = script };
        var actual = script;

        for (int i = 0; i < MaxPasadas; i++)
        {
            var analisis = _analizador.Analizar(actual);
            if (!analisis.ScriptValido || !analisis.Resultados.Any(r => r.Estado == EstadoRegla.NoCumple))
                break;

            var siguiente = AplicarPasada(actual, resultado.Cambios);
            if (siguiente == actual) break;
            actual = siguiente;
        }

        resultado.ScriptCorregido = actual;
        resultado.AnalisisCorregido = _analizador.Analizar(actual);
        return resultado;
    }

    private static string AplicarPasada(string script, List<CambioAplicado> cambios)
    {
        var (tablas, errores) = new LectorSql().Leer(script);
        if (errores.Count > 0) return script;

        var textos = LectorSql.DividirLotes(script);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);

        var lotes = new List<TSqlScript>();
        foreach (var texto in textos)
        {
            using var lector = new StringReader(texto);
            lotes.Add((TSqlScript)parser.Parse(lector, out _));
        }

        var modificado = new bool[lotes.Count];

        var corrector = new CorrectorVisitor(tablas, cambios);
        for (int i = 0; i < lotes.Count; i++)
            modificado[i] |= Visitar(lotes[i], corrector);

        var limpiador = new LimpiadorVisitor(corrector.RestriccionesQuitadas, cambios);
        for (int i = 0; i < lotes.Count; i++)
            modificado[i] |= Visitar(lotes[i], limpiador);

        if (!modificado.Contains(true)) return script;

        var generador = new Sql160ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
            IncludeSemicolons = true,
            AlignClauseBodies = false
        });

        // Solo se vuelve a generar el SQL de los lotes modificados; el resto queda tal como lo escribió el usuario
        var salida = new List<string>();
        for (int i = 0; i < lotes.Count; i++)
        {
            var texto = textos[i].Trim();
            if (modificado[i])
            {
                generador.GenerateScript(lotes[i], out var generado);
                texto = generado.Trim();
            }
            if (texto.Length > 0) salida.Add(texto);
        }

        var nl = Environment.NewLine;
        return string.Join($"{nl}GO{nl}", salida) + nl;
    }

    // Recorre el lote, borra las sentencias que el visitor marcó y dice si hubo cambios
    private static bool Visitar(TSqlScript lote, CorrectorVisitorBase visitor)
    {
        var antes = visitor.Contador;
        visitor.Preparar(lote);
        lote.Accept(visitor);

        foreach (var batch in lote.Batches)
            foreach (var sentencia in visitor.SentenciasAEliminar)
                batch.Statements.Remove(sentencia);

        return visitor.Contador != antes;
    }
}
