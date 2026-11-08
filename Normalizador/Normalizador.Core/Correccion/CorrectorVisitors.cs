using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Normalizador.Core.Modelos;
using Normalizador.Core.Reglas;

namespace Normalizador.Core.Correccion;

// Base comun: recuerda que sentencias están al nivel del lote (las únicas que se pueden eliminar sin dejar
// código inválido) y cuenta las modificaciones hechas.
internal abstract class CorrectorVisitorBase : TSqlFragmentVisitor
{
    protected readonly List<CambioAplicado> Cambios;
    private HashSet<TSqlStatement> _nivelLote = new();

    public List<TSqlStatement> SentenciasAEliminar { get; } = new();
    public int Contador { get; protected set; }

    protected CorrectorVisitorBase(List<CambioAplicado> cambios) => Cambios = cambios;

    public void Preparar(TSqlScript script)
    {
        SentenciasAEliminar.Clear();
        _nivelLote = script.Batches.SelectMany(b => b.Statements).ToHashSet();
    }

    protected bool EsNivelLote(TSqlStatement sentencia) => _nivelLote.Contains(sentencia);

    protected void Registrar(string tabla, string regla, string descripcion)
    {
        Cambios.Add(new CambioAplicado { Tabla = tabla, Regla = regla, Descripcion = descripcion });
        Contador++;
    }
}

// Fase 1: corrige CREATE TABLE y ALTER TABLE ... ADD según los prerrequisitos que no se cumplen
internal class CorrectorVisitor : CorrectorVisitorBase
{
    private static readonly Regex NombreSimple = new(@"^[A-Za-z_][A-Za-z0-9_]*$");

    private static readonly string ReglaPk = new ReglaClavePrimaria().Nombre;
    private static readonly string ReglaDuplicadas = new ReglaColumnasDuplicadas().Nombre;
    private static readonly string ReglaFk = new ReglaClavesForaneas().Nombre;

    private readonly IReadOnlyList<Tabla> _tablas;

    // "tabla|restriccion" de las claves foráneas eliminadas (para limpiar luego sus ALTER ... CHECK CONSTRAINT)
    public HashSet<string> RestriccionesQuitadas { get; } = new(StringComparer.OrdinalIgnoreCase);

    public CorrectorVisitor(IReadOnlyList<Tabla> tablas, List<CambioAplicado> cambios) : base(cambios)
    {
        _tablas = tablas;
    }

    public override void ExplicitVisit(CreateTableStatement nodo)
    {
        var def = nodo.Definition;
        if (def == null) return;

        var esquema = nodo.SchemaObjectName.SchemaIdentifier?.Value ?? "dbo";
        var nombreTabla = nodo.SchemaObjectName.BaseIdentifier.Value;
        var modelo = _tablas.FirstOrDefault(t =>
            t.Nombre.Equals(nombreTabla, StringComparison.OrdinalIgnoreCase) &&
            t.Esquema.Equals(esquema, StringComparison.OrdinalIgnoreCase));
        if (modelo == null) return;

        QuitarColumnasDuplicadas(def, nombreTabla);
        QuitarClavesForaneasInvalidas(def, modelo, nombreTabla);

        if (!modelo.TienePrimaryKey)
            AgregarClavePrimaria(def, nombreTabla);
    }

    public override void ExplicitVisit(AlterTableAddTableElementStatement nodo)
    {
        var nombreTabla = nodo.SchemaObjectName.BaseIdentifier.Value;
        var modelo = _tablas.FirstOrDefault(t => t.Nombre.Equals(nombreTabla, StringComparison.OrdinalIgnoreCase));
        var def = nodo.Definition;
        if (modelo == null || def == null) return;

        var invalidas = new List<(ForeignKeyConstraintDefinition Fk, List<string> Problemas)>();
        foreach (var fk in def.TableConstraints.OfType<ForeignKeyConstraintDefinition>())
        {
            var problemas = ReglaClavesForaneas.Validar(modelo, ConvertirFk(fk, null), _tablas);
            if (problemas.Count > 0) invalidas.Add((fk, problemas));
        }
        if (invalidas.Count == 0) return;

        // Si el ALTER solo contenía esas claves, hay que borrar la sentencia completa
        var quedaVacia = def.ColumnDefinitions.Count == 0
            && def.TableConstraints.Count == invalidas.Count
            && (def.Indexes == null || def.Indexes.Count == 0)
            && def.SystemTimePeriod == null;

        if (quedaVacia && !EsNivelLote(nodo)) return;

        foreach (var (fk, problemas) in invalidas)
        {
            if (!quedaVacia) def.TableConstraints.Remove(fk);
            RegistrarClaveForaneaQuitada(nombreTabla, fk, null, problemas);
        }

        if (quedaVacia) SentenciasAEliminar.Add(nodo);
    }

    // ---------- Regla: sin columnas duplicadas ----------
    private void QuitarColumnasDuplicadas(TableDefinition def, string tabla)
    {
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < def.ColumnDefinitions.Count; i++)
        {
            var nombre = def.ColumnDefinitions[i].ColumnIdentifier.Value;
            if (vistos.Add(nombre)) continue;

            def.ColumnDefinitions.RemoveAt(i--);
            Registrar(tabla, ReglaDuplicadas,
                $"Se eliminó la columna repetida '{nombre}' (se conservó la primera definición).");
        }
    }

    // ---------- Regla: claves foráneas válidas ----------
    private void QuitarClavesForaneasInvalidas(TableDefinition def, Tabla modelo, string tabla)
    {
        foreach (var columna in def.ColumnDefinitions)
        {
            for (int i = 0; i < columna.Constraints.Count; i++)
            {
                if (columna.Constraints[i] is not ForeignKeyConstraintDefinition fk) continue;

                var problemas = ReglaClavesForaneas.Validar(
                    modelo, ConvertirFk(fk, columna.ColumnIdentifier.Value), _tablas);
                if (problemas.Count == 0) continue;

                columna.Constraints.RemoveAt(i--);
                RegistrarClaveForaneaQuitada(tabla, fk, columna.ColumnIdentifier.Value, problemas);
            }
        }

        for (int i = 0; i < def.TableConstraints.Count; i++)
        {
            if (def.TableConstraints[i] is not ForeignKeyConstraintDefinition fk) continue;

            var problemas = ReglaClavesForaneas.Validar(modelo, ConvertirFk(fk, null), _tablas);
            if (problemas.Count == 0) continue;

            def.TableConstraints.RemoveAt(i--);
            RegistrarClaveForaneaQuitada(tabla, fk, null, problemas);
        }
    }

    private static ClaveForanea ConvertirFk(ForeignKeyConstraintDefinition fk, string? columnaLocal) => new()
    {
        Columnas = columnaLocal != null
            ? new List<string> { columnaLocal }
            : fk.Columns.Select(c => c.Value).ToList(),
        TablaReferenciada = fk.ReferenceTableName.BaseIdentifier.Value,
        ColumnasReferenciadas = fk.ReferencedTableColumns.Select(c => c.Value).ToList()
    };

    private void RegistrarClaveForaneaQuitada(string tabla, ForeignKeyConstraintDefinition fk,
        string? columnaLocal, List<string> problemas)
    {
        var local = columnaLocal ?? string.Join(", ", fk.Columns.Select(c => c.Value));
        var destino = $"{fk.ReferenceTableName.BaseIdentifier.Value}({string.Join(", ", fk.ReferencedTableColumns.Select(c => c.Value))})";

        var nombreRestriccion = fk.ConstraintIdentifier?.Value;
        if (nombreRestriccion != null) RestriccionesQuitadas.Add($"{tabla}|{nombreRestriccion}");

        Registrar(tabla, ReglaFk,
            $"Se quitó la clave foránea ({local}) → {destino} porque no era válida: {string.Join(" ", problemas)}");
    }

    // ---------- Regla: tiene clave primaria ----------
    private void AgregarClavePrimaria(TableDefinition def, string tabla)
    {
        var nombresRestricciones = def.TableConstraints
            .Concat(def.ColumnDefinitions.SelectMany(c => c.Constraints))
            .Select(c => c.ConstraintIdentifier?.Value)
            .Where(n => n != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var nombreRestriccion = NombreLibre($"PK_{tabla}", nombresRestricciones!);

        // SQL Server solo permite una columna IDENTITY por tabla: si ya existe, esa pasa a ser la clave.
        var identity = def.ColumnDefinitions.FirstOrDefault(c => c.IdentityOptions != null);
        if (identity != null)
        {
            var existente = identity.ColumnIdentifier.Value;
            def.TableConstraints.Add(CrearNodos(existente, nombreRestriccion).Restriccion);
            Registrar(tabla, ReglaPk,
                $"La tabla no tenía clave primaria: se definió '{existente}' (columna IDENTITY) como clave primaria '{nombreRestriccion}'.");
            return;
        }

        var nombresColumnas = def.ColumnDefinitions
            .Select(c => c.ColumnIdentifier.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columna = NombreLibre($"Id_{tabla}", nombresColumnas);

        var nodos = CrearNodos(columna, nombreRestriccion);
        def.ColumnDefinitions.Insert(0, nodos.Columna);
        def.TableConstraints.Add(nodos.Restriccion);

        Registrar(tabla, ReglaPk,
            $"La tabla no tenía clave primaria: se agregó la columna '{columna}' INT IDENTITY(1,1) como clave primaria '{nombreRestriccion}'.");
    }

    private static string NombreLibre(string baseNombre, ISet<string> ocupados)
    {
        var nombre = baseNombre;
        for (int n = 2; ocupados.Contains(nombre); n++)
            nombre = $"{baseNombre}_{n}";
        return nombre;
    }

    // Se arma el CREATE TABLE como texto y se deja que el parser cree los nodos: así el generador
    // los imprime exactamente igual que si el usuario los hubiera escrito.
    private static (ColumnDefinition Columna, ConstraintDefinition Restriccion) CrearNodos(string columna, string restriccion)
    {
        var sql = $"CREATE TABLE T ({Id(columna)} INT IDENTITY(1,1) NOT NULL, CONSTRAINT {Id(restriccion)} PRIMARY KEY ({Id(columna)}))";
        var script = (TSqlScript)new TSql160Parser(initialQuotedIdentifiers: true)
            .Parse(new StringReader(sql), out _);
        var create = (CreateTableStatement)script.Batches[0].Statements[0];

        return (create.Definition.ColumnDefinitions[0], create.Definition.TableConstraints[0]);
    }

    private static string Id(string nombre) =>
        NombreSimple.IsMatch(nombre) ? nombre : $"[{nombre.Replace("]", "]]")}]";
}

// Fase 2: SSMS genera "ALTER TABLE x CHECK CONSTRAINT FK_..." después de crear cada clave foránea.
// Si la clave se eliminó, esa sentencia quedaría apuntando a algo que ya no existe.
internal class LimpiadorVisitor : CorrectorVisitorBase
{
    private static readonly string ReglaFk = new ReglaClavesForaneas().Nombre;
    private readonly HashSet<string> _quitadas;

    public LimpiadorVisitor(HashSet<string> quitadas, List<CambioAplicado> cambios) : base(cambios)
    {
        _quitadas = quitadas;
    }

    public override void ExplicitVisit(AlterTableConstraintModificationStatement nodo)
    {
        if (nodo.ConstraintNames == null || nodo.ConstraintNames.Count == 0) return;

        var tabla = nodo.SchemaObjectName.BaseIdentifier.Value;
        var afectados = nodo.ConstraintNames.Where(n => _quitadas.Contains($"{tabla}|{n.Value}")).ToList();
        if (afectados.Count == 0) return;

        var todas = afectados.Count == nodo.ConstraintNames.Count;
        if (todas && !EsNivelLote(nodo)) return;

        var nombres = string.Join(", ", afectados.Select(n => n.Value));
        if (todas)
            SentenciasAEliminar.Add(nodo);
        else
            foreach (var n in afectados) nodo.ConstraintNames.Remove(n);

        Registrar(tabla, ReglaFk,
            $"Se quitó la referencia a la restricción eliminada ({nombres}) en un ALTER TABLE ... CONSTRAINT.");
    }
}
