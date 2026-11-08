using System.Text.RegularExpressions;
using Normalizador.Core.Modelos;

namespace Normalizador.Core.Servicios;

public class Normalizador2FN
{
    private const string NombreRegla2FN = "Sin dependencias funcionales parciales (2FN)";

    private static readonly string[] PrefijosClave =
        { "id_", "id", "cod_", "cod", "codigo_", "codigo", "num_", "num", "numero_", "numero", "fk_", "pk_" };

    private static readonly string[] AtributosRelacion =
        { "cantidad", "cant", "subtotal", "descuento", "nota", "calificacion", "puntaje", "horas" };

    private static readonly string[] AtributosCatalogoItem =
        { "precio", "costo", "valor", "tarifa", "docente", "profesor", "instructor", "categoria", "marca", "unidad", "creditos" };

    public ResultadoFormaNormal Normalizar(IReadOnlyList<Tabla> tablasEntrada)
    {
        var tablasBase = ClonarTablas(tablasEntrada);

        var resultado = new ResultadoFormaNormal
        {
            NombreFormaNormal = "Segunda Forma Normal (2FN)",
            EvaluacionesIniciales = Evaluar2FN(tablasBase)
        };

        var tablas = ClonarTablas(tablasBase);
        var nombresOcupados = tablas.Select(t => t.Nombre).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nuevasTablas = new List<Tabla>();

        foreach (var tabla in tablas)
        {
            var parciales = DetectarDependenciasParciales(tabla);
            if (parciales.Count == 0) continue;

            foreach (var (colDeterminante, dependientes) in parciales)
            {
                var nombresDep = string.Join(", ", dependientes.Select(c => c.Nombre));
                var raizEntidad = ExtraerRaizEntidad(colDeterminante.Nombre);
                var nombreBase = string.IsNullOrWhiteSpace(raizEntidad)
                    ? $"{tabla.Nombre}_{colDeterminante.Nombre}"
                    : Capitalizar(raizEntidad);
                var nombreNuevaTabla = NombreUnico(nombreBase, nombresOcupados);

                var nuevaTabla = new Tabla
                {
                    Esquema = tabla.Esquema,
                    Nombre = nombreNuevaTabla
                };

                var usarIdSustituto = !EsColumnaIdNumerica(colDeterminante);
                var nombrePkNueva = usarIdSustituto ? $"Id_{nombreNuevaTabla}" : colDeterminante.Nombre;

                if (usarIdSustituto)
                {
                    nuevaTabla.Columnas.Add(new Columna
                    {
                        Nombre = nombrePkNueva,
                        TipoDato = "INT",
                        PermiteNulos = false,
                        EsPrimaryKey = true,
                        EsIdentity = true
                    });

                    nuevaTabla.Columnas.Add(new Columna
                    {
                        Nombre = colDeterminante.Nombre,
                        TipoDato = colDeterminante.TipoDato,
                        PermiteNulos = false,
                        EsPrimaryKey = false,
                        EsUnica = true,
                        EsIdentity = false
                    });
                }
                else
                {
                    nuevaTabla.Columnas.Add(new Columna
                    {
                        Nombre = colDeterminante.Nombre,
                        TipoDato = colDeterminante.TipoDato,
                        PermiteNulos = false,
                        EsPrimaryKey = true,
                        EsUnica = colDeterminante.EsUnica,
                        EsIdentity = false
                    });
                }

                foreach (var dep in dependientes)
                {
                    nuevaTabla.Columnas.Add(new Columna
                    {
                        Nombre = dep.Nombre,
                        TipoDato = dep.TipoDato,
                        PermiteNulos = dep.PermiteNulos,
                        EsPrimaryKey = false,
                        EsUnica = dep.EsUnica,
                        EsIdentity = false
                    });
                }

                // Proyectar los datos hacia la nueva tabla y actualizar la FK en la tabla origen
                var mapaValorAId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var contadorId = 0;

                foreach (var filaPadre in tabla.Filas)
                {
                    var valorClave = filaPadre.TryGetValue(colDeterminante.Nombre, out var vk) && vk != null ? vk : "";
                    if (valorClave.Length > 0 && !mapaValorAId.TryGetValue(valorClave, out var idAsignado))
                    {
                        contadorId++;
                        idAsignado = usarIdSustituto ? contadorId.ToString() : valorClave;
                        mapaValorAId[valorClave] = idAsignado;

                        var filaNueva = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                        if (usarIdSustituto)
                        {
                            filaNueva[nombrePkNueva] = idAsignado;
                            filaNueva[colDeterminante.Nombre] = valorClave;
                        }
                        else
                        {
                            filaNueva[colDeterminante.Nombre] = valorClave;
                        }

                        foreach (var dep in dependientes)
                            filaNueva[dep.Nombre] = filaPadre.TryGetValue(dep.Nombre, out var vd) ? vd : null;

                        nuevaTabla.Filas.Add(filaNueva);
                    }

                    if (usarIdSustituto)
                    {
                        filaPadre.Remove(colDeterminante.Nombre);
                        filaPadre[nombrePkNueva] = mapaValorAId.TryGetValue(valorClave, out var idFk) ? idFk : "1";
                    }

                    foreach (var dep in dependientes)
                        filaPadre.Remove(dep.Nombre);
                }

                if (usarIdSustituto)
                {
                    colDeterminante.Nombre = nombrePkNueva;
                    colDeterminante.TipoDato = "INT";
                }

                foreach (var dep in dependientes)
                    tabla.Columnas.Remove(dep);

                var yaTieneFk = tabla.ClavesForaneas.Any(fk =>
                    fk.Columnas.Count == 1 &&
                    fk.Columnas[0].Equals(nombrePkNueva, StringComparison.OrdinalIgnoreCase));

                if (!yaTieneFk)
                {
                    tabla.ClavesForaneas.Add(new ClaveForanea
                    {
                        Columnas = new List<string> { nombrePkNueva },
                        TablaReferenciada = nuevaTabla.Nombre,
                        ColumnasReferenciadas = new List<string> { nombrePkNueva }
                    });
                }

                nuevasTablas.Add(nuevaTabla);

                resultado.Cambios.Add(new CambioAplicado
                {
                    Tabla = tabla.Nombre,
                    Regla = NombreRegla2FN,
                    Descripcion = $"Se eliminó la dependencia parcial extrayendo ({nombresDep}) a la nueva tabla '{nuevaTabla.Nombre}' con clave primaria '{nombrePkNueva}' y clave foránea desde '{tabla.Nombre}({nombrePkNueva})'."
                });
            }
        }

        tablas.AddRange(nuevasTablas);
        resultado.TablasNormalizadas = tablas;
        resultado.EvaluacionesFinales = Evaluar2FN(tablas);
        return resultado;
    }

    private static bool EsColumnaIdNumerica(Columna c)
    {
        var t = c.TipoDato.ToLowerInvariant();
        var esNum = t.Contains("int") || t.Contains("numeric") || t.Contains("bigint") || t.Contains("smallint");
        var esPrefijo = PrefijosClave.Any(p => c.Nombre.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || Regex.IsMatch(c.Nombre, @"(_id|_cod|_codigo)$", RegexOptions.IgnoreCase);
        return esNum || esPrefijo;
    }

    private static List<ResultadoRegla> Evaluar2FN(IReadOnlyList<Tabla> tablas)
    {
        var lista = new List<ResultadoRegla>();

        foreach (var tabla in tablas)
        {
            var pks = tabla.ClavePrimaria;
            var noClaves = tabla.Columnas.Where(c => !c.EsPrimaryKey).ToList();

            if (pks.Count <= 1)
            {
                var pkNombre = pks.Count == 1 ? pks[0].Nombre : "sin PK";
                lista.Add(ResultadoRegla.De(tabla, NombreRegla2FN, EstadoRegla.Cumple,
                    $"Cumple 2FN: tiene clave primaria simple ({pkNombre}), por lo que no existen dependencias parciales."));
                continue;
            }

            if (noClaves.Count == 0)
            {
                var pkComp = string.Join(", ", pks.Select(c => c.Nombre));
                lista.Add(ResultadoRegla.De(tabla, NombreRegla2FN, EstadoRegla.Cumple,
                    $"Cumple 2FN: clave primaria compuesta ({pkComp}) sin atributos no clave."));
                continue;
            }

            var parciales = DetectarDependenciasParciales(tabla);
            if (parciales.Count > 0)
            {
                var detalle = string.Join("; ",
                    parciales.Select(p => $"{p.Determinante.Nombre} → {string.Join(", ", p.Dependientes.Select(d => d.Nombre))}"));
                lista.Add(ResultadoRegla.De(tabla, NombreRegla2FN, EstadoRegla.NoCumple,
                    $"Dependencias parciales detectadas: {detalle}"));
            }
            else
            {
                var pkComp = string.Join(", ", pks.Select(c => c.Nombre));
                lista.Add(ResultadoRegla.De(tabla, NombreRegla2FN, EstadoRegla.Cumple,
                    $"Cumple 2FN: todos los atributos no clave dependen de la clave compuesta completa ({pkComp})."));
            }
        }

        return lista;
    }

    private static List<(Columna Determinante, List<Columna> Dependientes)> DetectarDependenciasParciales(Tabla tabla)
    {
        var resultado = new List<(Columna Determinante, List<Columna> Dependientes)>();
        var pks = tabla.ClavePrimaria;
        if (pks.Count <= 1) return resultado;

        var noClaves = tabla.Columnas.Where(c => !c.EsPrimaryKey).ToList();
        if (noClaves.Count == 0) return resultado;

        var raicesPk = pks
            .Select(pk => (Columna: pk, Raiz: ExtraerRaizEntidad(pk.Nombre)))
            .ToList();

        var asignadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pk, raiz) in raicesPk)
        {
            var dependientes = new List<Columna>();

            foreach (var col in noClaves)
            {
                if (asignadas.Contains(col.Nombre)) continue;

                var nombreLower = col.Nombre.ToLowerInvariant();
                if (AtributosRelacion.Any(a => nombreLower.Contains(a))) continue;

                var coincideConEstaRaiz = raiz.Length >= 3 &&
                    nombreLower.Contains(raiz, StringComparison.OrdinalIgnoreCase);

                var coincideConOtraRaiz = raicesPk
                    .Where(x => x.Columna != pk && x.Raiz.Length >= 3)
                    .Any(x => nombreLower.Contains(x.Raiz, StringComparison.OrdinalIgnoreCase));

                if (coincideConEstaRaiz && !coincideConOtraRaiz)
                {
                    dependientes.Add(col);
                    asignadas.Add(col.Nombre);
                    continue;
                }

                if (!coincideConOtraRaiz && CumpleDependenciaFuncionalEnFilas(tabla.Filas, pk.Nombre, col.Nombre))
                {
                    dependientes.Add(col);
                    asignadas.Add(col.Nombre);
                }
            }

            if (dependientes.Count > 0)
                resultado.Add((pk, dependientes));
        }

        var pkElemento = pks.Last();
        var extrasElemento = noClaves
            .Where(c => !asignadas.Contains(c.Nombre)
                && !AtributosRelacion.Any(a => c.Nombre.Contains(a, StringComparison.OrdinalIgnoreCase))
                && AtributosCatalogoItem.Any(cat => c.Nombre.Contains(cat, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (extrasElemento.Count > 0)
        {
            var existente = resultado.FirstOrDefault(r => r.Determinante == pkElemento);
            if (existente.Determinante != null)
                existente.Dependientes.AddRange(extrasElemento);
            else
                resultado.Add((pkElemento, extrasElemento));
        }

        return resultado;
    }

    private static bool CumpleDependenciaFuncionalEnFilas(
        List<Dictionary<string, string?>> filas,
        string colDeterminante,
        string colDependiente)
    {
        if (filas.Count < 2) return false;

        var mapa = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var valoresRepetidos = 0;

        foreach (var fila in filas)
        {
            if (!fila.TryGetValue(colDeterminante, out var x) || string.IsNullOrWhiteSpace(x))
                continue;

            var y = fila.TryGetValue(colDependiente, out var valY) ? valY : null;
            if (mapa.TryGetValue(x, out var yPrevio))
            {
                valoresRepetidos++;
                if (!string.Equals(yPrevio, y, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            else
            {
                mapa[x] = y;
            }
        }

        var distintosY = mapa.Values.Where(v => v != null).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return valoresRepetidos >= 1 && distintosY >= 2;
    }

    private static string ExtraerRaizEntidad(string nombreColumna)
    {
        var limpio = nombreColumna.Trim();
        foreach (var prefijo in PrefijosClave.OrderByDescending(p => p.Length))
        {
            if (limpio.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase) && limpio.Length > prefijo.Length)
            {
                limpio = limpio.Substring(prefijo.Length);
                break;
            }
        }

        limpio = Regex.Replace(limpio, @"(_?id|_?cod|_?codigo)$", "", RegexOptions.IgnoreCase).Trim('_');
        return limpio;
    }

    private static string Capitalizar(string texto) =>
        string.IsNullOrEmpty(texto) ? texto : char.ToUpperInvariant(texto[0]) + texto.Substring(1);

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
