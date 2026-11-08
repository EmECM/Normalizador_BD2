using System.Text.RegularExpressions;
using Normalizador.Core.Modelos;
using Normalizador.Core.Reglas;

namespace Normalizador.Core.Servicios;

public class Normalizador1FN
{
    private static readonly Regex PatronGrupo = new(@"^(.+?)_?(\d+)$");
    private static readonly char[] SeparadoresMultivalor = { ',', ';', '|' };
    private static readonly string[] TiposNoAtomicos = { "xml", "text", "ntext", "image" };
    private static readonly string[] PalabrasMultivalor = { "lista", "listado", "varios", "multiple", "csv", "items" };

    private static readonly IReglaTabla ReglaGrupos = new ReglaGruposRepetitivos();
    private static readonly IReglaTabla ReglaAtomico = new ReglaAtomicidad();

    public Resultado1FN Normalizar(IReadOnlyList<Tabla> tablasEntrada)
    {
        var tablasConDatos = ClonarTablas(tablasEntrada);
        foreach (var t in tablasConDatos)
            AsegurarFilasIniciales(t);

        var resultado = new Resultado1FN
        {
            EvaluacionesIniciales = Evaluar1FN(tablasConDatos)
        };

        var tablas = ClonarTablas(tablasConDatos);
        var nombresOcupados = tablas.Select(t => t.Nombre).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nuevasTablas = new List<Tabla>();

        foreach (var tabla in tablas)
        {
            ResolverGruposRepetitivos(tabla, nombresOcupados, nuevasTablas, resultado.Cambios);
            ResolverColumnasNoAtomicas(tabla, nombresOcupados, nuevasTablas, resultado.Cambios);
        }

        tablas.AddRange(nuevasTablas);
        resultado.Tablas1FN = tablas;
        resultado.EvaluacionesFinales = Evaluar1FN(tablas);
        return resultado;
    }

    // En la fase de 1FN, las advertencias de grupos repetitivos y multivalores pasan a ser "NoCumple"
    private static List<ResultadoRegla> Evaluar1FN(IReadOnlyList<Tabla> tablas)
    {
        var lista = new List<ResultadoRegla>();
        foreach (var tabla in tablas)
        {
            foreach (var regla in new[] { ReglaGrupos, ReglaAtomico })
            {
                var eval = regla.Evaluar(tabla, tablas);
                if (eval.Estado == EstadoRegla.Advertencia)
                    eval.Estado = EstadoRegla.NoCumple;
                lista.Add(eval);
            }
        }
        return lista;
    }

    private static void ResolverGruposRepetitivos(
        Tabla tabla,
        ISet<string> nombresTablas,
        List<Tabla> nuevasTablas,
        List<CambioAplicado> cambios)
    {
        var grupos = tabla.Columnas
            .Where(c => !c.EsPrimaryKey)
            .Select(c => (Columna: c, Coincidencia: PatronGrupo.Match(c.Nombre)))
            .Where(x => x.Coincidencia.Success)
            .GroupBy(x => x.Coincidencia.Groups[1].Value.TrimEnd('_'), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .ToList();

        foreach (var grupo in grupos)
        {
            var columnasGrupo = grupo.Select(x => x.Columna).ToList();
            var nombresOriginales = string.Join(", ", columnasGrupo.Select(c => c.Nombre));

            var nombreColumnaAtomica = LimpiarNombreAtomico(grupo.Key);
            var tipoAtomico = ConvertirTipoAtomico(columnasGrupo[0].TipoDato);
            var (nuevaTabla, mapeoPk, nombreColumnaValor) = CrearTablaHija(
                tabla, nombreColumnaAtomica, tipoAtomico, nombresTablas);

            // Migrar los datos de cada columna repetitiva hacia filas individuales de la tabla hija
            foreach (var filaPadre in tabla.Filas)
            {
                foreach (var colRep in columnasGrupo)
                {
                    if (filaPadre.TryGetValue(colRep.Nombre, out var valor) && !string.IsNullOrWhiteSpace(valor))
                    {
                        var filaHija = CrearFilaBaseHija(filaPadre, mapeoPk);
                        filaHija[nombreColumnaValor] = valor.Trim();
                        AgregarFilaSiNoDuplicada(nuevaTabla, filaHija);
                    }
                    filaPadre.Remove(colRep.Nombre);
                }
            }

            foreach (var col in columnasGrupo)
                tabla.Columnas.Remove(col);

            nuevasTablas.Add(nuevaTabla);
            var pkCompuesta = string.Join(", ", nuevaTabla.ClavePrimaria.Select(c => c.Nombre));

            cambios.Add(new CambioAplicado
            {
                Tabla = tabla.Nombre,
                Regla = ReglaGrupos.Nombre,
                Descripcion = $"Se eliminaron las columnas repetitivas ({nombresOriginales}) y se creó la tabla '{nuevaTabla.Nombre}' con clave primaria compuesta ({pkCompuesta}) y clave foránea hacia '{tabla.Nombre}'."
            });
        }
    }

    private static void ResolverColumnasNoAtomicas(
        Tabla tabla,
        ISet<string> nombresTablas,
        List<Tabla> nuevasTablas,
        List<CambioAplicado> cambios)
    {
        var noAtomicas = tabla.Columnas
            .Where(c => !c.EsPrimaryKey && (EsColumnaNoAtomica(c) || TieneDatosMultivalor(tabla, c.Nombre)))
            .ToList();

        foreach (var col in noAtomicas)
        {
            var nombreLower = col.Nombre.ToLowerInvariant();
            var tipoLower = col.TipoDato.ToLowerInvariant();
            var tieneValoresSeparados = TieneDatosMultivalor(tabla, col.Nombre);
            var esMultivalor = PalabrasMultivalor.Any(p => nombreLower.Contains(p))
                || tipoLower == "xml"
                || tieneValoresSeparados;

            if (esMultivalor)
            {
                var nombreColumnaAtomica = LimpiarNombreAtomico(col.Nombre);
                var tipoAtomico = ConvertirTipoAtomico(col.TipoDato);
                var (nuevaTabla, mapeoPk, nombreColumnaValor) = CrearTablaHija(
                    tabla, nombreColumnaAtomica, tipoAtomico, nombresTablas);

                // Desglosar los valores separados por coma/punto y coma en registros individuales
                foreach (var filaPadre in tabla.Filas)
                {
                    if (filaPadre.TryGetValue(col.Nombre, out var celda) && !string.IsNullOrWhiteSpace(celda))
                    {
                        var partes = celda.Split(SeparadoresMultivalor, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var parte in partes)
                        {
                            var limpio = parte.Trim();
                            if (limpio.Length == 0) continue;
                            var filaHija = CrearFilaBaseHija(filaPadre, mapeoPk);
                            filaHija[nombreColumnaValor] = limpio;
                            AgregarFilaSiNoDuplicada(nuevaTabla, filaHija);
                        }
                    }
                    filaPadre.Remove(col.Nombre);
                }

                tabla.Columnas.Remove(col);
                nuevasTablas.Add(nuevaTabla);
                var pkCompuesta = string.Join(", ", nuevaTabla.ClavePrimaria.Select(c => c.Nombre));

                cambios.Add(new CambioAplicado
                {
                    Tabla = tabla.Nombre,
                    Regla = ReglaAtomico.Nombre,
                    Descripcion = $"Se extrajo la columna multivaluada '{col.Nombre}' ({col.TipoDato}) a la nueva tabla '{nuevaTabla.Nombre}' con clave primaria compuesta ({pkCompuesta}) y clave foránea hacia '{tabla.Nombre}'."
                });
            }
            else
            {
                var tipoAnterior = col.TipoDato;
                col.TipoDato = ConvertirTipoAtomico(col.TipoDato);

                cambios.Add(new CambioAplicado
                {
                    Tabla = tabla.Nombre,
                    Regla = ReglaAtomico.Nombre,
                    Descripcion = $"Se normalizó el tipo de dato de la columna '{col.Nombre}' de '{tipoAnterior}' a '{col.TipoDato}' para garantizar valores atómicos."
                });
            }
        }
    }

    private static (Tabla Hija, List<(string ColPadre, string ColHija)> MapeoPk, string ColumnaValor) CrearTablaHija(
        Tabla padre,
        string nombreValorBase,
        string tipoValor,
        ISet<string> nombresTablas)
    {
        var nombreTabla = NombreUnico($"{padre.Nombre}_{nombreValorBase}", nombresTablas);
        var hija = new Tabla
        {
            Esquema = padre.Esquema,
            Nombre = nombreTabla
        };

        var columnasFk = new List<string>();
        var columnasRef = new List<string>();
        var mapeoPk = new List<(string ColPadre, string ColHija)>();

        foreach (var pk in padre.ClavePrimaria)
        {
            var nombreFk = pk.Nombre.Equals("Id", StringComparison.OrdinalIgnoreCase)
                ? $"Id_{padre.Nombre}"
                : pk.Nombre;

            hija.Columnas.Add(new Columna
            {
                Nombre = nombreFk,
                TipoDato = ConvertirTipoAtomico(pk.TipoDato),
                PermiteNulos = false,
                EsPrimaryKey = true,
                EsIdentity = false
            });

            columnasFk.Add(nombreFk);
            columnasRef.Add(pk.Nombre);
            mapeoPk.Add((pk.Nombre, nombreFk));
        }

        var ocupadosColumna = hija.Columnas.Select(c => c.Nombre).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nombreColumnaValor = ocupadosColumna.Contains(nombreValorBase)
            ? $"Valor_{nombreValorBase}"
            : nombreValorBase;

        hija.Columnas.Add(new Columna
        {
            Nombre = nombreColumnaValor,
            TipoDato = tipoValor,
            PermiteNulos = false,
            EsPrimaryKey = true,
            EsIdentity = false
        });

        if (columnasFk.Count > 0)
        {
            hija.ClavesForaneas.Add(new ClaveForanea
            {
                Columnas = columnasFk,
                TablaReferenciada = padre.Nombre,
                ColumnasReferenciadas = columnasRef
            });
        }

        return (hija, mapeoPk, nombreColumnaValor);
    }

    private static Dictionary<string, string?> CrearFilaBaseHija(
        Dictionary<string, string?> filaPadre,
        List<(string ColPadre, string ColHija)> mapeoPk)
    {
        var fila = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (colPadre, colHija) in mapeoPk)
            fila[colHija] = filaPadre.TryGetValue(colPadre, out var v) ? v : "1";
        return fila;
    }

    private static void AgregarFilaSiNoDuplicada(Tabla tabla, Dictionary<string, string?> nueva)
    {
        var claves = tabla.ClavePrimaria.Select(c => c.Nombre).ToList();
        var existe = tabla.Filas.Any(f =>
            claves.All(k => string.Equals(
                f.TryGetValue(k, out var v1) ? v1 : null,
                nueva.TryGetValue(k, out var v2) ? v2 : null,
                StringComparison.OrdinalIgnoreCase)));

        if (!existe)
            tabla.Filas.Add(nueva);
    }

    private static bool TieneDatosMultivalor(Tabla tabla, string nombreColumna) =>
        tabla.Filas.Any(f =>
            f.TryGetValue(nombreColumna, out var v)
            && !string.IsNullOrWhiteSpace(v)
            && v.IndexOfAny(SeparadoresMultivalor) >= 0);

    private static bool EsColumnaNoAtomica(Columna c)
    {
        var tipo = c.TipoDato.ToLowerInvariant();
        var nombre = c.Nombre.ToLowerInvariant();
        return TiposNoAtomicos.Contains(tipo)
            || tipo.Contains("(max)")
            || PalabrasMultivalor.Any(p => nombre.Contains(p));
    }

    // Si el script solo contenía CREATE TABLE (sin INSERT) y la tabla no existe aún en SQL Server,
    // genera 10 registros coherentes con las columnas para visualizar las tablas en 1FN con datos.
    private static void AsegurarFilasIniciales(Tabla tabla)
    {
        if (tabla.Filas.Count > 0) return;

        for (int i = 1; i <= 10; i++)
        {
            var fila = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in tabla.Columnas)
                fila[col.Nombre] = GenerarValorEjemplo(col, i);
            tabla.Filas.Add(fila);
        }
    }

    private static string GenerarValorEjemplo(Columna col, int fila)
    {
        var n = col.Nombre.ToLowerInvariant();
        var t = col.TipoDato.ToLowerInvariant();

        if (col.EsPrimaryKey || col.EsIdentity || n.StartsWith("id") || n.StartsWith("codigo"))
            return fila.ToString();

        var matchGrupo = PatronGrupo.Match(col.Nombre);
        if (matchGrupo.Success)
        {
            var baseNom = matchGrupo.Groups[1].Value.TrimEnd('_');
            var numGrupo = matchGrupo.Groups[2].Value;
            if (baseNom.Contains("tel", StringComparison.OrdinalIgnoreCase))
                return $"220{fila}-100{numGrupo}";
            return $"{baseNom} {fila}.{numGrupo}";
        }

        if (PalabrasMultivalor.Any(p => n.Contains(p)))
        {
            var limpio = LimpiarNombreAtomico(col.Nombre);
            return $"{limpio} {fila}A, {limpio} {fila}B";
        }

        if (t.Contains("int") || t.Contains("numeric") || t.Contains("decimal") || t.Contains("money") || t.Contains("float"))
            return (fila * 10).ToString();
        if (t.Contains("date") || t.Contains("time"))
            return $"2026-01-{fila:00}";
        if (t.Contains("bit"))
            return (fila % 2 == 0 ? "1" : "0");

        if (n.Contains("nombre")) return $"Nombre {fila}";
        if (n.Contains("nacionalidad") || n.Contains("pais"))
        {
            string[] paises = { "Honduras", "México", "España", "Colombia", "Argentina", "Chile", "Perú", "Costa Rica", "Guatemala", "Panamá" };
            return paises[(fila - 1) % paises.Length];
        }
        if (n.Contains("institucion") || n.Contains("empresa")) return $"Institución {fila}";
        if (n.Contains("correo") || n.Contains("email")) return $"usuario{fila}@correo.com";
        if (n.Contains("tel")) return $"9900-00{fila:00}";

        return $"{col.Nombre} {fila}";
    }

    private static string LimpiarNombreAtomico(string nombre)
    {
        var limpio = PatronGrupo.Replace(nombre, "$1");
        foreach (var palabra in PalabrasMultivalor.OrderByDescending(p => p.Length))
            limpio = Regex.Replace(limpio, palabra, "", RegexOptions.IgnoreCase);

        limpio = Regex.Replace(limpio, @"_+_+", "_").Trim('_', ' ');

        if (string.IsNullOrWhiteSpace(limpio) || PatronGrupo.IsMatch(limpio))
            return "Elemento";

        return limpio;
    }

    private static string ConvertirTipoAtomico(string tipoDato)
    {
        var t = tipoDato.Trim().ToLowerInvariant();
        if (t == "image") return "VARBINARY(255)";
        if (t == "text" || (t.StartsWith("varchar") && t.Contains("(max)"))) return "VARCHAR(150)";
        if (TiposNoAtomicos.Contains(t) || t.Contains("(max)")) return "NVARCHAR(150)";
        return tipoDato;
    }

    private static string NombreUnico(string baseNombre, ISet<string> ocupados)
    {
        var candidato = baseNombre;
        for (int n = 2; !ocupados.Add(candidato); n++)
            candidato = $"{baseNombre}_{n}";
        return candidato;
    }

    private static List<Tabla> ClonarTablas(IReadOnlyList<Tabla> origen) =>
        origen.Select(t => new Tabla
        {
            Esquema = t.Esquema,
            Nombre = t.Nombre,
            Columnas = t.Columnas.Select(c => new Columna
            {
                Nombre = c.Nombre,
                TipoDato = c.TipoDato,
                PermiteNulos = c.PermiteNulos,
                EsPrimaryKey = c.EsPrimaryKey,
                EsUnica = c.EsUnica,
                EsIdentity = c.EsIdentity
            }).ToList(),
            ClavesForaneas = t.ClavesForaneas.Select(fk => new ClaveForanea
            {
                Columnas = new List<string>(fk.Columnas),
                TablaReferenciada = fk.TablaReferenciada,
                ColumnasReferenciadas = new List<string>(fk.ColumnasReferenciadas)
            }).ToList(),
            Filas = t.Filas.Select(f => new Dictionary<string, string?>(f, StringComparer.OrdinalIgnoreCase)).ToList()
        }).ToList();
}
