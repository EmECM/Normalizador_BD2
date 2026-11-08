using Microsoft.Data.SqlClient;
using Normalizador.Core.Modelos;

namespace Normalizador.Core.Servicios;

public class ServicioConexion
{
    public (bool Exito, string Mensaje) Probar(string cadenaConexion)
    {
        try
        {
            using var cn = new SqlConnection(cadenaConexion);
            cn.Open();
            return (true, $"Conectado a {cn.DataSource} (versión {cn.ServerVersion})");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // Si el archivo .sql no traía sentencias INSERT para alguna tabla, intenta leer los primeros registros
    // directamente de SQL Server (buscando la tabla en las bases de datos de usuario del servidor).
    public void CargarFilasSiExisten(string cadenaConexion, IReadOnlyList<Tabla> tablas)
    {
        if (string.IsNullOrWhiteSpace(cadenaConexion) || tablas.All(t => t.Filas.Count > 0))
            return;

        try
        {
            var builder = new SqlConnectionStringBuilder(cadenaConexion) { ConnectTimeout = 3 };
            using var cn = new SqlConnection(builder.ConnectionString);
            cn.Open();

            var bases = ObtenerBasesDeUsuario(cn);

            foreach (var tabla in tablas.Where(t => t.Filas.Count == 0))
            {
                foreach (var bd in bases)
                {
                    if (IntentarLeerTabla(cn, bd, tabla))
                        break;
                }
            }
        }
        catch
        {
            // Si no hay conexión o la tabla no existe en el servidor, Normalizador1FN usa las filas del script o de ejemplo
        }
    }

    private static List<string> ObtenerBasesDeUsuario(SqlConnection cn)
    {
        var lista = new List<string>();
        using var cmd = new SqlCommand(
            "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY create_date DESC", cn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            lista.Add(reader.GetString(0));
        return lista;
    }

    private static bool IntentarLeerTabla(SqlConnection cn, string bd, Tabla tabla)
    {
        try
        {
            var sqlCheck = $"SELECT COUNT(1) FROM [{bd}].INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @esq AND TABLE_NAME = @nom";
            using (var check = new SqlCommand(sqlCheck, cn))
            {
                check.Parameters.AddWithValue("@esq", tabla.Esquema);
                check.Parameters.AddWithValue("@nom", tabla.Nombre);
                if (Convert.ToInt32(check.ExecuteScalar()) == 0)
                    return false;
            }

            var sqlSelect = $"SELECT TOP 20 * FROM [{bd}].[{tabla.Esquema}].[{tabla.Nombre}]";
            using var cmd = new SqlCommand(sqlSelect, cn);
            using var reader = cmd.ExecuteReader();

            var numFila = 0;
            while (reader.Read())
            {
                numFila++;
                var fila = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var nombreCol = reader.GetName(i);
                    fila[nombreCol] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i));
                }

                foreach (var col in tabla.Columnas.Where(c => c.EsIdentity || c.EsPrimaryKey))
                {
                    if (!fila.ContainsKey(col.Nombre) || string.IsNullOrWhiteSpace(fila[col.Nombre]))
                        fila[col.Nombre] = numFila.ToString();
                }

                tabla.Filas.Add(fila);
            }

            return tabla.Filas.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
