using Microsoft.SqlServer.TransactSql.ScriptDom;
using Normalizador.Core.Modelos;

namespace Normalizador.Core.Lectura;

internal class EstructuraVisitor : TSqlFragmentVisitor
{
    private const int MaxFilasPorTabla = 50;

    public List<Tabla> Tablas { get; } = new();
    private readonly List<AlterTableAddTableElementStatement> _alters = new();
    private readonly List<InsertStatement> _inserts = new();

    public override void ExplicitVisit(CreateTableStatement nodo)
    {
        var tabla = new Tabla
        {
            Esquema = nodo.SchemaObjectName.SchemaIdentifier?.Value ?? "dbo",
            Nombre = nodo.SchemaObjectName.BaseIdentifier.Value
        };

        foreach (var def in nodo.Definition.ColumnDefinitions)
        {
            var col = new Columna
            {
                Nombre = def.ColumnIdentifier.Value,
                TipoDato = def.DataType == null ? "(calculada)" : Texto(def.DataType),
                EsIdentity = def.IdentityOptions != null
            };
            tabla.Columnas.Add(col);

            foreach (var restriccion in def.Constraints)
                AplicarRestriccion(tabla, col, restriccion);
        }

        foreach (var restriccion in nodo.Definition.TableConstraints)
            AplicarRestriccion(tabla, null, restriccion);

        Tablas.Add(tabla);
    }

    public override void ExplicitVisit(AlterTableAddTableElementStatement nodo)
    {
        _alters.Add(nodo);
    }

    public override void ExplicitVisit(InsertStatement nodo)
    {
        _inserts.Add(nodo);
    }

    // Se llama al final, cuando ya se leyeron todas las tablas
    public void AplicarAlters()
    {
        foreach (var alter in _alters)
        {
            var nombre = alter.SchemaObjectName.BaseIdentifier.Value;
            var tabla = Tablas.FirstOrDefault(t => t.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase));
            if (tabla == null) continue;

            foreach (var restriccion in alter.Definition.TableConstraints)
                AplicarRestriccion(tabla, null, restriccion);
        }

        AplicarInserts();
    }

    private void AplicarInserts()
    {
        foreach (var insert in _inserts)
        {
            var spec = insert.InsertSpecification;
            if (spec?.Target is not NamedTableReference destino) continue;
            if (spec.InsertSource is not ValuesInsertSource fuente) continue;

            var nombreTabla = destino.SchemaObject.BaseIdentifier.Value;
            var tabla = Tablas.FirstOrDefault(t => t.Nombre.Equals(nombreTabla, StringComparison.OrdinalIgnoreCase));
            if (tabla == null || tabla.Filas.Count >= MaxFilasPorTabla) continue;

            var columnasExplicitas = spec.Columns
                .Select(c => c.MultiPartIdentifier.Identifiers.Last().Value)
                .ToList();

            foreach (var row in fuente.RowValues)
            {
                if (tabla.Filas.Count >= MaxFilasPorTabla) break;

                var nombresDestino = ObtenerColumnasInsert(tabla, columnasExplicitas, row.ColumnValues.Count);
                var fila = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < row.ColumnValues.Count && i < nombresDestino.Count; i++)
                    fila[nombresDestino[i]] = ExtraerValor(row.ColumnValues[i]);

                var numeroFila = (tabla.Filas.Count + 1).ToString();
                foreach (var col in tabla.Columnas.Where(c => c.EsIdentity || c.EsPrimaryKey))
                {
                    if (!fila.ContainsKey(col.Nombre) || string.IsNullOrWhiteSpace(fila[col.Nombre]))
                        fila[col.Nombre] = numeroFila;
                }

                tabla.Filas.Add(fila);
            }
        }
    }

    private static List<string> ObtenerColumnasInsert(
        Tabla tabla,
        List<string> explicitas,
        int cantidadValores)
    {
        if (explicitas.Count > 0) return explicitas;

        var sinIdentity = tabla.Columnas.Where(c => !c.EsIdentity).Select(c => c.Nombre).ToList();
        if (cantidadValores == sinIdentity.Count)
            return sinIdentity;

        return tabla.Columnas.Select(c => c.Nombre).ToList();
    }

    private static string? ExtraerValor(ScalarExpression expr) => expr switch
    {
        NullLiteral => null,
        StringLiteral s => s.Value,
        IntegerLiteral i => i.Value,
        NumericLiteral n => n.Value,
        RealLiteral r => r.Value,
        MoneyLiteral m => m.Value,
        UnaryExpression u => Texto(u).Trim(),
        _ => Texto(expr).Trim()
    };

    private static void AplicarRestriccion(Tabla tabla, Columna? col, ConstraintDefinition restriccion)
    {
        switch (restriccion)
        {
            case NullableConstraintDefinition n when col != null:
                col.PermiteNulos = n.Nullable;
                break;

            case UniqueConstraintDefinition u:
                var nombres = col != null
                    ? new List<string> { col.Nombre }
                    : u.Columns.Select(c => c.Column.MultiPartIdentifier.Identifiers.Last().Value).ToList();

                foreach (var nombre in nombres)
                {
                    var objetivo = tabla.Columnas.FirstOrDefault(
                        x => x.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase));
                    if (objetivo == null) continue;

                    if (u.IsPrimaryKey)
                    {
                        objetivo.EsPrimaryKey = true;
                        objetivo.PermiteNulos = false;
                    }
                    else if (nombres.Count == 1)
                    {
                        objetivo.EsUnica = true;
                    }
                }
                break;

            case ForeignKeyConstraintDefinition f:
                tabla.ClavesForaneas.Add(new ClaveForanea
                {
                    Columnas = col != null
                        ? new List<string> { col.Nombre }
                        : f.Columns.Select(c => c.Value).ToList(),
                    TablaReferenciada = f.ReferenceTableName.BaseIdentifier.Value,
                    ColumnasReferenciadas = f.ReferencedTableColumns.Select(c => c.Value).ToList()
                });
                break;
        }
    }

    private static string Texto(TSqlFragment fragmento) =>
        string.Concat(fragmento.ScriptTokenStream
            .Skip(fragmento.FirstTokenIndex)
            .Take(fragmento.LastTokenIndex - fragmento.FirstTokenIndex + 1)
            .Select(t => t.Text));
}
