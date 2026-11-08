using System.Text.RegularExpressions;
using Normalizador.Core.Modelos;

namespace Normalizador.Core.Servicios;

public class Normalizador3FN
{
    private const string NombreRegla3FN = "Sin dependencias funcionales transitivas (3FN)";

    private static readonly string[] PrefijosClave =
        { "id_", "id", "cod_", "cod", "codigo_", "codigo", "num_", "num", "numero_", "numero", "fk_" };

    private static readonly string[] PrefijosAtributo =
        { "nombre_", "nom_", "descripcion_", "desc_", "direccion_", "dir_", "telefono_", "tel_", "correo_", "email_", "ubicacion_", "pais_", "ciudad_", "estado_", "tipo_", "jefe_", "facultad_" };

    private static readonly string[] EntidadesTercero =
        { "cliente", "proveedor", "vendedor", "institucion", "editorial", "departamento", "depto", "facultad", "carrera", "sucursal", "empresa" };

    private static readonly string[] AtributosDependientesTercero =
        { "direccion", "domicilio", "ubicacion", "localizacion", "ciudad", "pais", "nacionalidad", "telefono", "correo", "email" };

    public ResultadoFormaNormal Normalizar(IReadOnlyList<Tabla> tablasEntrada)
    {
        var tablasBase = ClonarTablas(tablasEntrada);

        var resultado = new ResultadoFormaNormal
        {
            NombreFormaNormal = "Tercera Forma Normal (3FN)",
            EvaluacionesIniciales = Evaluar3FN(tablasBase)
        };

        var tablas = ClonarTablas(tablasBase);
        var nombresOcupados = tablas.Select(t => t.Nombre).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nuevasTablas = new List<Tabla>();

        foreach (var tabla in tablas)
        {
            var transitivas = DetectarDependenciasTransitivas(tabla);
            if (transitivas.Count == 0) continue;

            foreach (var (determinante, dependientes, entidadSugerida) in transitivas)
            {
                var nombresDep = string.Join(", ", dependientes.Select(c => c.Nombre));
                var nombreNuevaTabla = NombreUnico(Capitalizar(entidadSugerida), nombresOcupados);

                var nuevaTabla = new Tabla
                {
                    Esquema = tabla.Esquema,
                    Nombre = nombreNuevaTabla
                };

                var usarIdSustituto = !EsColumnaIdentificadora(determinante.Nombre, out _);
                var nombrePkNueva = usarIdSustituto ? $"Id_{nombreNuevaTabla}" : determinante.Nombre;

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
                        Nombre = determinante.Nombre,
                        TipoDato = determinante.TipoDato,
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
                        Nombre = determinante.Nombre,
                        TipoDato = determinante.TipoDato,
                        PermiteNulos = false,
                        EsPrimaryKey = true,
                        EsUnica = true,
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

                // Proyectar filas únicas por la columna determinante hacia la nueva tabla catálogo
                var mapaValorAId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var contadorId = 0;

                foreach (var filaPadre in tabla.Filas)
                {
                    var valDet = filaPadre.TryGetValue(determinante.Nombre, out var vd) && vd != null ? vd : "";
                    if (valDet.Length > 0 && !mapaValorAId.TryGetValue(valDet, out var idAsignado))
                    {
                        contadorId++;
                        idAsignado = usarIdSustituto ? contadorId.ToString() : valDet;
                        mapaValorAId[valDet] = idAsignado;

                        var filaHija = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                        if (usarIdSustituto)
                        {
                            filaHija[nombrePkNueva] = idAsignado;
                            filaHija[determinante.Nombre] = valDet;
                        }
                        else
                        {
                            filaHija[determinante.Nombre] = valDet;
                        }

                        foreach (var dep in dependientes)
                            filaHija[dep.Nombre] = filaPadre.TryGetValue(dep.Nombre, out var v) ? v : null;

                        nuevaTabla.Filas.Add(filaHija);
                    }

                    if (usarIdSustituto)
                    {
                        filaPadre.Remove(determinante.Nombre);
                        filaPadre[nombrePkNueva] = mapaValorAId.TryGetValue(valDet, out var idFk) ? idFk : "1";
                    }

                    foreach (var dep in dependientes)
                        filaPadre.Remove(dep.Nombre);
                }

                var nombreOriginalDet = determinante.Nombre;
                if (usarIdSustituto)
                {
                    determinante.Nombre = nombrePkNueva;
                    determinante.TipoDato = "INT";
                    determinante.PermiteNulos = false;
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
                var pkTabla = string.Join(", ", tabla.ClavePrimaria.Select(c => c.Nombre));

                resultado.Cambios.Add(new CambioAplicado
                {
                    Tabla = tabla.Nombre,
                    Regla = NombreRegla3FN,
                    Descripcion = $"Se eliminó la dependencia transitiva ({pkTabla} → {nombreOriginalDet} → {nombresDep}) creando la tabla '{nuevaTabla.Nombre}' con clave primaria '{nombrePkNueva}' y clave foránea '{nombrePkNueva}' en '{tabla.Nombre}'."
                });
            }
        }

        tablas.AddRange(nuevasTablas);
        resultado.TablasNormalizadas = tablas;
        resultado.EvaluacionesFinales = Evaluar3FN(tablas);
        return resultado;
    }

    private static List<ResultadoRegla> Evaluar3FN(IReadOnlyList<Tabla> tablas)
    {
        var lista = new List<ResultadoRegla>();

        foreach (var tabla in tablas)
        {
            var noClaves = tabla.Columnas.Where(c => !c.EsPrimaryKey).ToList();
            if (noClaves.Count <= 1)
            {
                lista.Add(ResultadoRegla.De(tabla, NombreRegla3FN, EstadoRegla.Cumple,
                    "Cumple 3FN: no tiene suficientes atributos no clave para formar dependencias transitivas."));
                continue;
            }

            var transitivas = DetectarDependenciasTransitivas(tabla);
            if (transitivas.Count > 0)
            {
                var detalle = string.Join("; ",
                    transitivas.Select(t => $"{t.Determinante.Nombre} → {string.Join(", ", t.Dependientes.Select(d => d.Nombre))}"));
                lista.Add(ResultadoRegla.De(tabla, NombreRegla3FN, EstadoRegla.NoCumple,
                    $"Dependencias transitivas detectadas: {detalle}"));
            }
            else
            {
                lista.Add(ResultadoRegla.De(tabla, NombreRegla3FN, EstadoRegla.Cumple,
                    "Cumple 3FN: todos los atributos no clave dependen únicamente de la clave primaria."));
            }
        }

        return lista;
    }

    private static List<(Columna Determinante, List<Columna> Dependientes, string Entidad)> DetectarDependenciasTransitivas(Tabla tabla)
    {
        var resultado = new List<(Columna Determinante, List<Columna> Dependientes, string Entidad)>();
        var noClaves = tabla.Columnas.Where(c => !c.EsPrimaryKey).ToList();
        if (noClaves.Count <= 1) return resultado;

        var usadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Buscar columnas no clave tipo identificador (ej. Id_Departamento, Codigo_Editorial)
        foreach (var col in noClaves)
        {
            if (usadas.Contains(col.Nombre)) continue;
            if (!EsColumnaIdentificadora(col.Nombre, out var raiz) || raiz.Length < 3) continue;
            if (raiz.Equals(tabla.Nombre, StringComparison.OrdinalIgnoreCase)) continue;

            var dependientes = noClaves
                .Where(otra => otra != col
                    && !usadas.Contains(otra.Nombre)
                    && otra.Nombre.Contains(raiz, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (dependientes.Count > 0)
            {
                usadas.Add(col.Nombre);
                foreach (var d in dependientes) usadas.Add(d.Nombre);
                resultado.Add((col, dependientes, raiz));
            }
        }

        // 2. Buscar grupos de columnas no clave que compartan la misma entidad secundaria
        var restantes = noClaves.Where(c => !usadas.Contains(c.Nombre)).ToList();
        var porEntidad = restantes
            .Select(c => (Columna: c, Entidad: ExtraerEntidadSecundaria(c.Nombre, tabla.Nombre)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Entidad))
            .GroupBy(x => x.Entidad!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .ToList();

        foreach (var grupo in porEntidad)
        {
            var cols = grupo.Select(x => x.Columna).ToList();
            var det = cols[0];
            var deps = cols.Skip(1).ToList();

            usadas.Add(det.Nombre);
            foreach (var d in deps) usadas.Add(d.Nombre);
            resultado.Add((det, deps, grupo.Key));
        }

        // 3. Buscar atributos de una entidad tercero incrustada (ej. cliente_nombre -> direccion_envio)
        var disponibles = noClaves.Where(c => !usadas.Contains(c.Nombre)).ToList();
        foreach (var entidad in EntidadesTercero)
        {
            if (tabla.Nombre.Contains(entidad, StringComparison.OrdinalIgnoreCase)) continue;

            var colTercero = disponibles.FirstOrDefault(c =>
                !usadas.Contains(c.Nombre) &&
                c.Nombre.Contains(entidad, StringComparison.OrdinalIgnoreCase));

            if (colTercero == null) continue;

            var depsTercero = disponibles
                .Where(c => c != colTercero
                    && !usadas.Contains(c.Nombre)
                    && (c.Nombre.Contains(entidad, StringComparison.OrdinalIgnoreCase)
                        || AtributosDependientesTercero.Any(a => c.Nombre.Contains(a, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            if (depsTercero.Count > 0)
            {
                usadas.Add(colTercero.Nombre);
                foreach (var d in depsTercero) usadas.Add(d.Nombre);
                resultado.Add((colTercero, depsTercero, entidad));
            }
        }

        // 4. Verificar dependencias funcionales transitivas en los datos (X -> Y entre columnas no clave)
        var libres = noClaves.Where(c => !usadas.Contains(c.Nombre)).ToList();
        foreach (var det in libres)
        {
            if (usadas.Contains(det.Nombre)) continue;

            var deps = libres
                .Where(otra => otra != det
                    && !usadas.Contains(otra.Nombre)
                    && CumpleDependenciaTransitivaEnFilas(tabla.Filas, det.Nombre, otra.Nombre))
                .ToList();

            if (deps.Count > 0)
            {
                usadas.Add(det.Nombre);
                foreach (var d in deps) usadas.Add(d.Nombre);
                var entidad = ExtraerRaizLimpia(det.Nombre);
                resultado.Add((det, deps, string.IsNullOrWhiteSpace(entidad) ? $"{tabla.Nombre}_{det.Nombre}" : entidad));
            }
        }

        return resultado;
    }

    private static bool EsColumnaIdentificadora(string nombreColumna, out string raiz)
    {
        var limpio = nombreColumna.Trim();
        foreach (var prefijo in PrefijosClave.OrderByDescending(p => p.Length))
        {
            if (limpio.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase) && limpio.Length > prefijo.Length)
            {
                raiz = limpio.Substring(prefijo.Length).Trim('_');
                return true;
            }
        }

        var matchSufijo = Regex.Match(limpio, @"^(.+?)(?:_id|_cod|_codigo)$", RegexOptions.IgnoreCase);
        if (matchSufijo.Success)
        {
            raiz = matchSufijo.Groups[1].Value.Trim('_');
            return true;
        }

        raiz = "";
        return false;
    }

    private static string? ExtraerEntidadSecundaria(string nombreColumna, string nombreTabla)
    {
        foreach (var prefijo in PrefijosAtributo)
        {
            if (nombreColumna.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase) &&
                nombreColumna.Length > prefijo.Length)
            {
                var candidato = nombreColumna.Substring(prefijo.Length).Trim('_');
                if (candidato.Length >= 3 && !candidato.Equals(nombreTabla, StringComparison.OrdinalIgnoreCase))
                    return candidato;
            }
        }

        return null;
    }

    private static string ExtraerRaizLimpia(string nombreColumna)
    {
        if (EsColumnaIdentificadora(nombreColumna, out var r) && r.Length >= 3)
            return r;
        return nombreColumna.Trim('_');
    }

    private static string Capitalizar(string texto) =>
        string.IsNullOrEmpty(texto) ? texto : char.ToUpperInvariant(texto[0]) + texto.Substring(1);

    private static bool CumpleDependenciaTransitivaEnFilas(
        List<Dictionary<string, string?>> filas,
        string colDeterminante,
        string colDependiente)
    {
        if (filas.Count < 3) return false;

        var mapa = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var repeticiones = 0;

        foreach (var fila in filas)
        {
            if (!fila.TryGetValue(colDeterminante, out var x) || string.IsNullOrWhiteSpace(x))
                continue;

            var y = fila.TryGetValue(colDependiente, out var valY) ? valY : null;
            if (mapa.TryGetValue(x, out var yPrevio))
            {
                repeticiones++;
                if (!string.Equals(yPrevio, y, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            else
            {
                mapa[x] = y;
            }
        }

        var distintosX = mapa.Count;
        var distintosY = mapa.Values.Where(v => v != null).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return repeticiones >= 2 && distintosX >= 2 && distintosY >= 2 && distintosX < filas.Count;
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
