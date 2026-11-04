using Microsoft.Data.SqlClient;

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
}