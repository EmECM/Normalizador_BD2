using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Normalizador.Core.Modelos;
using Normalizador.Core.Servicios;

namespace Normalizador.Web.Pages;

public class AnalizarModel : PageModel
{
    private readonly AnalizadorPrerrequisitos _analizador;
    private readonly ServicioConexion _conexion;
    private readonly IConfiguration _config;

    public AnalizarModel(AnalizadorPrerrequisitos analizador, ServicioConexion conexion, IConfiguration config)
    {
        _analizador = analizador;
        _conexion = conexion;
        _config = config;
    }

    [BindProperty]
    public IFormFile? ArchivoSql { get; set; }

    public ResultadoAnalisis? Resultado { get; set; }
    public string? MensajeError { get; set; }
    public bool ConexionOk { get; set; }
    public string EstadoConexion { get; set; } = "";

    public void OnGet()
    {
        ProbarConexion();
    }

    public async Task OnPostAsync()
    {
        ProbarConexion();

        if (ArchivoSql == null || ArchivoSql.Length == 0)
        {
            MensajeError = "Selecciona un archivo .sql.";
            return;
        }

        if (!Path.GetExtension(ArchivoSql.FileName).Equals(".sql", StringComparison.OrdinalIgnoreCase))
        {
            MensajeError = "El archivo debe tener extensión .sql.";
            return;
        }

        using var lector = new StreamReader(ArchivoSql.OpenReadStream(), detectEncodingFromByteOrderMarks: true);
        var script = await lector.ReadToEndAsync();

        Resultado = _analizador.Analizar(script);
    }

    private void ProbarConexion()
    {
        var cadena = _config.GetConnectionString("SqlServer") ?? "";
        (ConexionOk, EstadoConexion) = _conexion.Probar(cadena);
    }
}