using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Normalizador.Core.Modelos;

namespace Normalizador.Core.Lectura;

public class LectorSql
{
    // El parser no entiende "GO", así que dividimos el script en lotes
    private static readonly Regex SeparadorGo =
        new(@"^[ \t]*GO[ \t]*(?:--.*)?\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // Lotes no vacíos del script, sin las líneas "GO"
    public static IReadOnlyList<string> DividirLotes(string script) =>
        SeparadorGo.Split(script).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

    public (List<Tabla> Tablas, List<string> Errores) Leer(string script)
    {
        var errores = new List<string>();
        var visitor = new EstructuraVisitor();
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);

        var lotes = SeparadorGo.Split(script);
        int numeroLote = 0;

        foreach (var lote in lotes)
        {
            if (string.IsNullOrWhiteSpace(lote)) continue;
            numeroLote++;

            using var lector = new StringReader(lote);
            var fragmento = parser.Parse(lector, out IList<ParseError> erroresParser);

            if (erroresParser.Count > 0)
            {
                foreach (var e in erroresParser)
                    errores.Add($"Lote {numeroLote}, línea {e.Line}, columna {e.Column}: {e.Message}");
                continue;
            }

            fragmento.Accept(visitor);
        }

        visitor.AplicarAlters();
        return (visitor.Tablas, errores);
    }
}