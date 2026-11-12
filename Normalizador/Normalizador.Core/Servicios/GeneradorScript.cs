using System.Text;
using Normalizador.Core.Modelos;

namespace Normalizador.Core.Servicios;

// Genera el script SQL (CREATE TABLE + INSERT) de un conjunto de tablas ya normalizadas
public static class GeneradorScript
{
    public static string Generar(IReadOnlyList<Tabla> tablas)
    {
        var sb = new StringBuilder();
        var ordenadas = OrdenarPorDependencias(tablas);

        sb.AppendLine("-- Script generado automaticamente por el Normalizador");
        sb.AppendLine();

        foreach (var t in ordenadas)
            GenerarCreate(sb, t);

        foreach (var t in ordenadas)
            GenerarInserts(sb, t);

        return sb.ToString();
    }

    private static List<Tabla> OrdenarPorDependencias(IReadOnlyList<Tabla> tablas)
    {
        var resultado = new List<Tabla>();
        var pendientes = tablas.ToList();

        while (pendientes.Count > 0)
        {
            var listas = pendientes.Where(t => t.ClavesForaneas.All(fk =>
                fk.TablaReferenciada.Equals(t.Nombre, StringComparison.OrdinalIgnoreCase)
                || resultado.Any(r => r.Nombre.Equals(fk.TablaReferenciada, StringComparison.OrdinalIgnoreCase))
                || !pendientes.Any(p => p.Nombre.Equals(fk.TablaReferenciada, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            if (listas.Count == 0) listas = new List<Tabla> { pendientes[0] };

            resultado.AddRange(listas);
            foreach (var l in listas) pendientes.Remove(l);
        }

        return resultado;
    }

    private static void GenerarCreate(StringBuilder sb, Tabla t)
    {
        var lineas = new List<string>();

        foreach (var c in t.Columnas)
        {
            var linea = $"    [{c.Nombre}] {c.TipoDato}";
            if (c.EsIdentity) linea += " IDENTITY(1,1)";
            linea += c.PermiteNulos && !c.EsPrimaryKey ? " NULL" : " NOT NULL";
            if (c.EsUnica && !c.EsPrimaryKey) linea += " UNIQUE";
            lineas.Add(linea);
        }

        var pks = t.ClavePrimaria;
        if (pks.Count > 0)
            lineas.Add($"    CONSTRAINT [PK_{t.Nombre}] PRIMARY KEY ({string.Join(", ", pks.Select(c => $"[{c.Nombre}]"))})");

        var n = 0;
        foreach (var fk in t.ClavesForaneas)
        {
            n++;
            lineas.Add($"    CONSTRAINT [FK_{t.Nombre}_{fk.TablaReferenciada}_{n}] FOREIGN KEY ({string.Join(", ", fk.Columnas.Select(c => $"[{c}]"))}) " +
                       $"REFERENCES [{t.Esquema}].[{fk.TablaReferenciada}] ({string.Join(", ", fk.ColumnasReferenciadas.Select(c => $"[{c}]"))})");
        }

        sb.AppendLine($"CREATE TABLE [{t.Esquema}].[{t.Nombre}] (");
        sb.AppendLine(string.Join("," + Environment.NewLine, lineas));
        sb.AppendLine(");");
        sb.AppendLine("GO");
        sb.AppendLine();
    }

    private static void GenerarInserts(StringBuilder sb, Tabla t)
    {
        if (t.Filas.Count == 0) return;

        var tieneIdentity = t.Columnas.Any(c => c.EsIdentity);
        var cols = string.Join(", ", t.Columnas.Select(c => $"[{c.Nombre}]"));

        if (tieneIdentity) sb.AppendLine($"SET IDENTITY_INSERT [{t.Esquema}].[{t.Nombre}] ON;");

        foreach (var fila in t.Filas)
        {
            var valores = t.Columnas.Select(c =>
                fila.TryGetValue(c.Nombre, out var v) ? Formatear(c, v) : "NULL");
            sb.AppendLine($"INSERT INTO [{t.Esquema}].[{t.Nombre}] ({cols}) VALUES ({string.Join(", ", valores)});");
        }

        if (tieneIdentity) sb.AppendLine($"SET IDENTITY_INSERT [{t.Esquema}].[{t.Nombre}] OFF;");

        sb.AppendLine("GO");
        sb.AppendLine();
    }

    private static string Formatear(Columna c, string? valor)
    {
        if (valor == null) return "NULL";

        var tipo = c.TipoDato.ToLowerInvariant();
        var numerico = tipo.Contains("int") || tipo.Contains("decimal") || tipo.Contains("numeric")
            || tipo.Contains("float") || tipo.Contains("real") || tipo.Contains("money") || tipo.Contains("bit");

        if (numerico && decimal.TryParse(valor, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return valor.Trim();

        return $"N'{valor.Replace("'", "''")}'";
    }
}
