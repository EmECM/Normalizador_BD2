using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Normalizador.Core.Modelos;
using Normalizador.Core.Servicios;

namespace Normalizador.Web.Pages;

public class AnalizarModel : PageModel
{
    private readonly AnalizadorPrerrequisitos _analizador;
    private readonly ServicioConexion _conexion;
    private readonly CorrectorPrerrequisitos _corrector;
    private readonly Normalizador1FN _normalizador1FN;
    private readonly IConfiguration _config;

    public AnalizarModel(
        AnalizadorPrerrequisitos analizador,
        ServicioConexion conexion,
        CorrectorPrerrequisitos corrector,
        Normalizador1FN normalizador1FN,
        IConfiguration config)
    {
        _analizador = analizador;
        _conexion = conexion;
        _corrector = corrector;
        _normalizador1FN = normalizador1FN;
        _config = config;
    }

    [BindProperty]
    public IFormFile? ArchivoSql { get; set; }

    public ResultadoAnalisis? Resultado { get; set; }
    public ResultadoCorreccion? Correccion { get; set; }
    public Resultado1FN? PrimeraFormaNormal { get; set; }
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

        // Si algún prerrequisito no se cumple, el sistema modifica el script para que sí cumpla
        if (Resultado.ScriptValido && Resultado.Resultados.Any(r => r.Estado == EstadoRegla.NoCumple))
            Correccion = _corrector.Corregir(script);

        // Fase 1FN: toma las tablas válidas (originales o ya corregidas en prerrequisitos)
        var analisisBase = Correccion?.AnalisisCorregido ?? Resultado;
        if (analisisBase.PuedeContinuar)
        {
            var cadena = _config.GetConnectionString("SqlServer") ?? "";
            _conexion.CargarFilasSiExisten(cadena, analisisBase.Tablas);
            PrimeraFormaNormal = _normalizador1FN.Normalizar(analisisBase.Tablas);
        }
    }

    private void ProbarConexion()
    {
        var cadena = _config.GetConnectionString("SqlServer") ?? "";
        (ConexionOk, EstadoConexion) = _conexion.Probar(cadena);
    }
}
