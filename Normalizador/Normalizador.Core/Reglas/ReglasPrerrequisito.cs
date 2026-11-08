using System.Text.RegularExpressions;
using Normalizador.Core.Modelos;

namespace Normalizador.Core.Reglas;

public class ReglaClavePrimaria : IReglaTabla
{
    public string Nombre => "Tiene clave primaria";

    public ResultadoRegla Evaluar(Tabla tabla, IReadOnlyList<Tabla> todas)
    {
        if (!tabla.TienePrimaryKey)
            return ResultadoRegla.De(tabla, Nombre, EstadoRegla.NoCumple,
                "La tabla no define ninguna clave primaria.");

        var cols = string.Join(", ", tabla.ClavePrimaria.Select(c => c.Nombre));
        return ResultadoRegla.De(tabla, Nombre, EstadoRegla.Cumple, $"Clave primaria: {cols}");
    }
}

public class ReglaColumnasDuplicadas : IReglaTabla
{
    public string Nombre => "Sin columnas duplicadas";

    public ResultadoRegla Evaluar(Tabla tabla, IReadOnlyList<Tabla> todas)
    {
        var duplicadas = tabla.Columnas
            .GroupBy(c => c.Nombre, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicadas.Count > 0)
            return ResultadoRegla.De(tabla, Nombre, EstadoRegla.NoCumple,
                $"Columnas repetidas: {string.Join(", ", duplicadas)}");

        return ResultadoRegla.De(tabla, Nombre, EstadoRegla.Cumple, "Todas las columnas tienen nombre único.");
    }
}

public class ReglaGruposRepetitivos : IReglaTabla
{
    private static readonly Regex Patron = new(@"^(.+?)_?(\d+)$");

    public string Nombre => "Sin grupos repetitivos (ej. telefono1, telefono2)";

    public ResultadoRegla Evaluar(Tabla tabla, IReadOnlyList<Tabla> todas)
    {
        var grupos = tabla.Columnas
            .Select(c => (c.Nombre, Coincidencia: Patron.Match(c.Nombre)))
            .Where(x => x.Coincidencia.Success)
            .GroupBy(x => x.Coincidencia.Groups[1].Value.TrimEnd('_'), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .ToList();

        if (grupos.Count > 0)
        {
            var detalle = string.Join("; ",
                grupos.Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Nombre))}"));
            return ResultadoRegla.De(tabla, Nombre, EstadoRegla.Advertencia,
                $"Posibles grupos repetitivos -> {detalle}");
        }

        return ResultadoRegla.De(tabla, Nombre, EstadoRegla.Cumple, "No se detectaron grupos repetitivos.");
    }
}

public class ReglaAtomicidad : IReglaTabla
{
    private static readonly char[] SeparadoresLista = { ',', ';', '|' };
    private static readonly string[] TiposSospechosos = { "xml", "text", "ntext", "image" };
    private static readonly string[] PalabrasSospechosas =
        { "lista", "listado", "varios", "multiple", "csv", "items", "telefonos", "correos", "productoscomprados", "cantidades", "preciosunitarios", "asignaturas", "docentes" };
    private static readonly string[] ColumnasTextoLibre =
        { "direccion", "domicilio", "ubicacion", "observacion", "descripcion", "comentario", "detalle_dir" };

    public string Nombre => "Valores atómicos (sin multivalores)";

    public ResultadoRegla Evaluar(Tabla tabla, IReadOnlyList<Tabla> todas)
    {
        var sospechosas = tabla.Columnas.Where(c => EsSospechosa(tabla, c))
            .Select(c => $"{c.Nombre} ({c.TipoDato})")
            .ToList();

        if (sospechosas.Count > 0)
            return ResultadoRegla.De(tabla, Nombre, EstadoRegla.Advertencia,
                $"Columnas que contienen o podrían guardar varios valores: {string.Join(", ", sospechosas)}");

        return ResultadoRegla.De(tabla, Nombre, EstadoRegla.Cumple, "No se detectaron columnas multivaluadas.");
    }

    public static bool EsSospechosa(Tabla tabla, Columna c)
    {
        var tipo = c.TipoDato.ToLowerInvariant();
        var nombre = c.Nombre.ToLowerInvariant();

        if (TiposSospechosos.Contains(tipo) || tipo.Contains("(max)"))
            return true;

        if (PalabrasSospechosas.Any(p => nombre.Contains(p)))
            return true;

        if (ColumnasTextoLibre.Any(p => nombre.Contains(p)))
            return false;

        return tabla.Filas.Any(f =>
            f.TryGetValue(c.Nombre, out var v)
            && !string.IsNullOrWhiteSpace(v)
            && v.IndexOfAny(SeparadoresLista) >= 0);
    }
}

public class ReglaClavesForaneas : IReglaTabla
{
    public string Nombre => "Claves foráneas válidas";

    public ResultadoRegla Evaluar(Tabla tabla, IReadOnlyList<Tabla> todas)
    {
        if (tabla.ClavesForaneas.Count == 0)
            return ResultadoRegla.De(tabla, Nombre, EstadoRegla.Cumple, "La tabla no tiene claves foráneas.");

        var problemas = new List<string>();

        foreach (var fk in tabla.ClavesForaneas)
            problemas.AddRange(Validar(tabla, fk, todas));

        if (problemas.Count > 0)
            return ResultadoRegla.De(tabla, Nombre, EstadoRegla.NoCumple, string.Join(" ", problemas));

        return ResultadoRegla.De(tabla, Nombre, EstadoRegla.Cumple,
            $"{tabla.ClavesForaneas.Count} clave(s) foránea(s) correcta(s).");
    }

    public static List<string> Validar(Tabla tabla, ClaveForanea fk, IReadOnlyList<Tabla> todas)
    {
        var problemas = new List<string>();

        var destino = todas.FirstOrDefault(
            t => t.Nombre.Equals(fk.TablaReferenciada, StringComparison.OrdinalIgnoreCase));

        if (destino == null)
        {
            problemas.Add($"La tabla referenciada '{fk.TablaReferenciada}' no existe en el script.");
            return problemas;
        }

        foreach (var nombre in fk.ColumnasReferenciadas)
            if (!destino.Columnas.Any(c => c.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase)))
                problemas.Add($"La columna '{nombre}' no existe en '{destino.Nombre}'.");

        foreach (var nombre in fk.Columnas)
            if (!tabla.Columnas.Any(c => c.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase)))
                problemas.Add($"La columna '{nombre}' no existe en '{tabla.Nombre}'.");

        return problemas;
    }
}
