using System.Text.RegularExpressions;
using Normalizador.Core.Modelos;
using Normalizador.Core.Reglas;

namespace Normalizador.Core.Servicios;

public class Normalizador1FN
{
    private static readonly Regex PatronGrupo = new(@"^(.+?)_?(\d+)$");
    private static readonly char[] SeparadoresMultivalor = { ',', ';', '|' };
    private static readonly string[] TiposNoAtomicos = { "xml", "text", "ntext", "image" };
    private static readonly string[] PalabrasMultivalor =
        { "lista", "listado", "varios", "multiple", "csv", "items", "telefonos", "correos", "productoscomprados", "cantidades", "preciosunitarios", "asignaturas", "docentes" };
    private static readonly string[] ColumnasTextoLibre =
        { "direccion", "domicilio", "ubicacion", "observacion", "descripcion", "comentario", "detalle_dir" };

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
            .Select(x => (
                x.Columna,
                Prefijo: x.Coincidencia.Groups[1].Value.TrimEnd('_'),
                Indice: x.Coincidencia.Groups[2].Value))
            .GroupBy(x => x.Prefijo, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .ToList();

        if (grupos.Count == 0) return;

        // Agrupar los prefijos que comparten los mismos índices numéricos (ej. producto_1..3, cantidad_1..3, precio_1..3)
        // en una sola tabla detalle con clave compuesta (PK_Padre, PrimerPrefijo) y el resto como atributos no clave.
        var porIndices = grupos
            .GroupBy(g => string.Join(",", g.Select(x => x.Indice).OrderBy(i => i)))
            .ToList();

        foreach (var bloque in porIndices)
        {
            var prefijos = bloque.ToList();
            var indices = prefijos[0].Select(x => x.Indice).Distinct().ToList();
            var todasColumnasBloque = prefijos.SelectMany(p => p.Select(x => x.Columna)).ToList();
            var nombresOriginales = string.Join(", ", todasColumnasBloque.Select(c => c.Nombre));

            var primerPrefijo = LimpiarNombreAtomico(prefijos[0].Key);
            var tipoPrimerPrefijo = ConvertirTipoAtomico(prefijos[0].First().Columna.TipoDato);

            var (nuevaTabla, mapeoPk, colClaveHija) = CrearTablaHija(
                tabla, primerPrefijo, tipoPrimerPrefijo, nombresTablas);

            var columnasAtributoHija = new List<(string PrefijoOriginal, string NombreColumnaHija)>();
            columnasAtributoHija.Add((prefijos[0].Key, colClaveHija));

            foreach (var prefijoExtra in prefijos.Skip(1))
            {
                var nombreLimpio = LimpiarNombreAtomico(prefijoExtra.Key);
                var ocupados = nuevaTabla.Columnas.Select(c => c.Nombre).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var nombreCol = ocupados.Contains(nombreLimpio) ? $"Valor_{nombreLimpio}" : nombreLimpio;

                nuevaTabla.Columnas.Add(new Columna
                {
                    Nombre = nombreCol,
                    TipoDato = ConvertirTipoAtomico(prefijoExtra.First().Columna.TipoDato),
                    PermiteNulos = true,
                    EsPrimaryKey = false,
                    EsIdentity = false
                });

                columnasAtributoHija.Add((prefijoExtra.Key, nombreCol));
            }

            // Migrar datos por cada índice (1, 2, 3...)
            foreach (var filaPadre in tabla.Filas)
            {
                foreach (var idx in indices)
                {
                    var filaHija = CrearFilaBaseHija(filaPadre, mapeoPk);
                    var tieneAlgunValor = false;

                    for (int p = 0; p < prefijos.Count; p++)
                    {
                        var colOriginal = prefijos[p].FirstOrDefault(x => x.Indice == idx).Columna;
                        var nombreHija = columnasAtributoHija[p].NombreColumnaHija;
                        if (colOriginal != null &&
                            filaPadre.TryGetValue(colOriginal.Nombre, out var v) &&
                            !string.IsNullOrWhiteSpace(v) &&
                            !v.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                        {
                            filaHija[nombreHija] = v.Trim();
                            if (p == 0) tieneAlgunValor = true;
                        }
                        else
                        {
                            filaHija[nombreHija] = null;
                        }
                    }

                    if (tieneAlgunValor)
                        AgregarFilaSiNoDuplicada(nuevaTabla, filaHija);
                }

                foreach (var col in todasColumnasBloque)
                    filaPadre.Remove(col.Nombre);
            }

            foreach (var col in todasColumnasBloque)
                tabla.Columnas.Remove(col);

            nuevasTablas.Add(nuevaTabla);
            var pkCompuesta = string.Join(", ", nuevaTabla.ClavePrimaria.Select(c => c.Nombre));
            var colsHija = string.Join(", ", nuevaTabla.Columnas.Select(c => c.Nombre));

            cambios.Add(new CambioAplicado
            {
                Tabla = tabla.Nombre,
                Regla = ReglaGrupos.Nombre,
                Descripcion = $"Se eliminaron las columnas repetitivas ({nombresOriginales}) y se creó la tabla '{nuevaTabla.Nombre}' ({colsHija}) con clave primaria compuesta ({pkCompuesta}) y clave foránea hacia '{tabla.Nombre}'."
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
            .Where(c => !c.EsPrimaryKey && ReglaAtomicidad.EsSospechosa(tabla, c))
            .ToList();

        if (noAtomicas.Count == 0) return;

        var multivalor = new List<Columna>();
        var soloTipoNoAtomico = new List<Columna>();

        foreach (var col in noAtomicas)
        {
            var nombreLower = col.Nombre.ToLowerInvariant();
            var tipoLower = col.TipoDato.ToLowerInvariant();
            if (PalabrasMultivalor.Any(p => nombreLower.Contains(p))
                || tipoLower == "xml"
                || TieneDatosMultivalor(tabla, col.Nombre))
            {
                multivalor.Add(col);
            }
            else
            {
                soloTipoNoAtomico.Add(col);
            }
        }

        // 1. Separar columnas de contacto independientes (ej. telefonos, correos) de listas paralelas de detalle
        var contactosIndependientes = multivalor
            .Where(c => c.Nombre.Contains("tel", StringComparison.OrdinalIgnoreCase)
                     || c.Nombre.Contains("correo", StringComparison.OrdinalIgnoreCase)
                     || c.Nombre.Contains("email", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var paralelasCandidatas = multivalor.Except(contactosIndependientes).ToList();

        foreach (var col in contactosIndependientes)
            ExtraerColumnaMultivalorIndividual(tabla, col, nombresTablas, nuevasTablas, cambios);

        if (paralelasCandidatas.Count >= 2 && SonListasParalelas(tabla, paralelasCandidatas))
        {
            ExtraerColumnasMultivalorParalelas(tabla, paralelasCandidatas, nombresTablas, nuevasTablas, cambios);
        }
        else
        {
            foreach (var col in paralelasCandidatas)
                ExtraerColumnaMultivalorIndividual(tabla, col, nombresTablas, nuevasTablas, cambios);
        }

        foreach (var col in soloTipoNoAtomico)
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

    private static bool SonListasParalelas(Tabla tabla, List<Columna> columnas)
    {
        if (tabla.Filas.Count == 0) return true;

        foreach (var fila in tabla.Filas)
        {
            var longitudes = columnas
                .Select(c => fila.TryGetValue(c.Nombre, out var v) && !string.IsNullOrWhiteSpace(v)
                    ? v.Split(SeparadoresMultivalor, StringSplitOptions.RemoveEmptyEntries).Length
                    : 0)
                .Distinct()
                .ToList();

            if (longitudes.Count > 1)
                return false;
        }

        return true;
    }

    private static void ExtraerColumnasMultivalorParalelas(
        Tabla tabla,
        List<Columna> columnas,
        ISet<string> nombresTablas,
        List<Tabla> nuevasTablas,
        List<CambioAplicado> cambios)
    {
        var primera = columnas[0];
        var nombreClaveHija = LimpiarNombreAtomico(primera.Nombre);
        var tipoClaveHija = InferirTipoElemento(tabla, primera);

        var (nuevaTabla, mapeoPk, colClaveHija) = CrearTablaHija(
            tabla, nombreClaveHija, tipoClaveHija, nombresTablas);

        var nombresHija = new List<string> { colClaveHija };

        foreach (var colExtra in columnas.Skip(1))
        {
            var limpio = LimpiarNombreAtomico(colExtra.Nombre);
            var ocupados = nuevaTabla.Columnas.Select(c => c.Nombre).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nombreFinal = ocupados.Contains(limpio) ? $"Valor_{limpio}" : limpio;

            nuevaTabla.Columnas.Add(new Columna
            {
                Nombre = nombreFinal,
                TipoDato = InferirTipoElemento(tabla, colExtra),
                PermiteNulos = false,
                EsPrimaryKey = false,
                EsIdentity = false
            });

            nombresHija.Add(nombreFinal);
        }

        foreach (var filaPadre in tabla.Filas)
        {
            var listasPorColumna = columnas.Select(c =>
                filaPadre.TryGetValue(c.Nombre, out var celda) && !string.IsNullOrWhiteSpace(celda)
                    ? celda.Split(SeparadoresMultivalor, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList()
                    : new List<string>())
                .ToList();

            var maxItems = listasPorColumna.Max(l => l.Count);
            for (int idx = 0; idx < maxItems; idx++)
            {
                var filaHija = CrearFilaBaseHija(filaPadre, mapeoPk);
                for (int c = 0; c < columnas.Count; c++)
                {
                    var lista = listasPorColumna[c];
                    filaHija[nombresHija[c]] = idx < lista.Count ? lista[idx] : null;
                }

                if (!string.IsNullOrWhiteSpace(filaHija[colClaveHija]))
                    AgregarFilaSiNoDuplicada(nuevaTabla, filaHija);
            }

            foreach (var col in columnas)
                filaPadre.Remove(col.Nombre);
        }

        foreach (var col in columnas)
            tabla.Columnas.Remove(col);

        nuevasTablas.Add(nuevaTabla);
        var nombresOrig = string.Join(", ", columnas.Select(c => c.Nombre));
        var pkCompuesta = string.Join(", ", nuevaTabla.ClavePrimaria.Select(c => c.Nombre));
        var colsNuevas = string.Join(", ", nuevaTabla.Columnas.Select(c => c.Nombre));

        cambios.Add(new CambioAplicado
        {
            Tabla = tabla.Nombre,
            Regla = ReglaAtomico.Nombre,
            Descripcion = $"Se desglosaron las columnas multivaluadas ({nombresOrig}) en la nueva tabla '{nuevaTabla.Nombre}' ({colsNuevas}) con clave primaria compuesta ({pkCompuesta}) y clave foránea hacia '{tabla.Nombre}'."
        });
    }

    private static void ExtraerColumnaMultivalorIndividual(
        Tabla tabla,
        Columna col,
        ISet<string> nombresTablas,
        List<Tabla> nuevasTablas,
        List<CambioAplicado> cambios)
    {
        var nombreColumnaAtomica = LimpiarNombreAtomico(col.Nombre);
        var tipoAtomico = InferirTipoElemento(tabla, col);
        var (nuevaTabla, mapeoPk, nombreColumnaValor) = CrearTablaHija(
            tabla, nombreColumnaAtomica, tipoAtomico, nombresTablas);

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

    private static string InferirTipoElemento(Tabla tabla, Columna col)
    {
        var n = col.Nombre.ToLowerInvariant();
        if (n.Contains("cantidad")) return "INT";
        if (n.Contains("precio") || n.Contains("costo") || n.Contains("monto")) return "DECIMAL(10, 2)";
        return ConvertirTipoAtomico(col.TipoDato);
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

    private static bool TieneDatosMultivalor(Tabla tabla, string nombreColumna)
    {
        if (ColumnasTextoLibre.Any(p => nombreColumna.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return false;

        return tabla.Filas.Any(f =>
            f.TryGetValue(nombreColumna, out var v)
            && !string.IsNullOrWhiteSpace(v)
            && v.IndexOfAny(SeparadoresMultivalor) >= 0);
    }

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
            string[] paises = { "Honduras", "México", "España", "Honduras", "México" };
            return paises[(fila - 1) % paises.Length];
        }
        if (n.Contains("institucion") || n.Contains("empresa"))
        {
            string[] inst = { "UNAH", "CEUTEC", "UNAM", "UNAH", "CEUTEC" };
            return inst[(fila - 1) % inst.Length];
        }
        if (n.Contains("correo") || n.Contains("email")) return $"usuario{fila}@correo.com";
        if (n.Contains("tel")) return $"9900-00{fila:00}";

        return $"{col.Nombre} {fila}";
    }

    private static string LimpiarNombreAtomico(string nombre)
    {
        var limpio = PatronGrupo.Replace(nombre, "$1");
        if (limpio.Equals("ProductosComprados", StringComparison.OrdinalIgnoreCase)) return "Producto";
        if (limpio.Equals("PreciosUnitarios", StringComparison.OrdinalIgnoreCase)) return "Precio_Unitario";
        if (limpio.Equals("Cantidades", StringComparison.OrdinalIgnoreCase)) return "Cantidad";
        if (limpio.Equals("AsignaturasMatriculadas", StringComparison.OrdinalIgnoreCase)) return "Asignatura";
        if (limpio.Equals("DocentesAsignados", StringComparison.OrdinalIgnoreCase)) return "Docente";
        if (limpio.Equals("TelefonoCliente", StringComparison.OrdinalIgnoreCase) ||
            limpio.Equals("cliente_telefonos", StringComparison.OrdinalIgnoreCase)) return "Telefono";

        foreach (var palabra in new[] { "lista", "listado", "varios", "multiple", "csv", "items" })
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
