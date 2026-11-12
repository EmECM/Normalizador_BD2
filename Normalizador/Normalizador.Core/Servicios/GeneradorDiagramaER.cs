using System.Net;
using System.Text;
using Normalizador.Core.Modelos;

namespace Normalizador.Core.Servicios;

// Dibuja un diagrama entidad-relacion sencillo en SVG (sin librerias externas)
public static class GeneradorDiagramaER
{
    private const int Ancho = 230;
    private const int AltoTitulo = 30;
    private const int AltoFila = 20;
    private const int Separacion = 70;

    public static string Generar(IReadOnlyList<Tabla> tablas)
    {
        if (tablas.Count == 0) return "";

        var columnas = (int)Math.Ceiling(Math.Sqrt(tablas.Count * 1.5));
        columnas = Math.Max(1, Math.Min(columnas, tablas.Count));

        var cajas = new Dictionary<string, (double X, double Y, double W, double H)>(StringComparer.OrdinalIgnoreCase);
        var y = (double)Separacion / 2;
        double anchoTotal = 0;

        for (int inicio = 0; inicio < tablas.Count; inicio += columnas)
        {
            var fila = tablas.Skip(inicio).Take(columnas).ToList();
            double altoMax = 0;
            for (int i = 0; i < fila.Count; i++)
            {
                var alto = AltoTitulo + fila[i].Columnas.Count * AltoFila + 6;
                var x = Separacion / 2 + i * (Ancho + Separacion);
                cajas[fila[i].Nombre] = (x, y, Ancho, alto);
                altoMax = Math.Max(altoMax, alto);
                anchoTotal = Math.Max(anchoTotal, x + Ancho + Separacion / 2);
            }
            y += altoMax + Separacion;
        }

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{anchoTotal:0}\" height=\"{y:0}\" viewBox=\"0 0 {anchoTotal:0} {y:0}\" font-family=\"Segoe UI, Arial, sans-serif\" font-size=\"12\">");

        // Relaciones (debajo de las cajas)
        foreach (var t in tablas)
        {
            foreach (var fk in t.ClavesForaneas)
            {
                if (!cajas.TryGetValue(t.Nombre, out var a) || !cajas.TryGetValue(fk.TablaReferenciada, out var b)) continue;
                if (t.Nombre.Equals(fk.TablaReferenciada, StringComparison.OrdinalIgnoreCase)) continue;

                var (x1, y1) = Borde(a, b);
                var (x2, y2) = Borde(b, a);
                sb.Append($"<line x1=\"{x1:0}\" y1=\"{y1:0}\" x2=\"{x2:0}\" y2=\"{y2:0}\" stroke=\"#546e7a\" stroke-width=\"1.5\"/>");
                sb.Append(Etiqueta(x1, y1, x2, y2, "N"));
                sb.Append(Etiqueta(x2, y2, x1, y1, "1"));
            }
        }

        foreach (var t in tablas)
        {
            var (x, yy, w, h) = cajas[t.Nombre];
            sb.Append($"<rect x=\"{x:0}\" y=\"{yy:0}\" width=\"{w:0}\" height=\"{h:0}\" fill=\"#ffffff\" stroke=\"#37474f\" stroke-width=\"1.5\" rx=\"4\"/>");
            sb.Append($"<rect x=\"{x:0}\" y=\"{yy:0}\" width=\"{w:0}\" height=\"{AltoTitulo}\" fill=\"#37474f\" rx=\"4\"/>");
            sb.Append($"<text x=\"{x + w / 2:0}\" y=\"{yy + 20:0}\" fill=\"#ffffff\" text-anchor=\"middle\" font-weight=\"bold\" font-size=\"13\">{WebUtility.HtmlEncode(t.Nombre)}</text>");

            var i = 0;
            foreach (var c in t.Columnas)
            {
                var esFk = t.ClavesForaneas.Any(f => f.Columnas.Any(col => col.Equals(c.Nombre, StringComparison.OrdinalIgnoreCase)));
                var marca = (c.EsPrimaryKey ? "PK" : "") + (c.EsPrimaryKey && esFk ? ",": "") + (esFk ? "FK" : "");
                var cy = yy + AltoTitulo + (i + 1) * AltoFila - 5;
                var peso = c.EsPrimaryKey ? "bold" : "normal";
                sb.Append($"<text x=\"{x + 8:0}\" y=\"{cy:0}\" font-weight=\"{peso}\" fill=\"#212121\">{WebUtility.HtmlEncode(c.Nombre)}</text>");
                if (marca.Length > 0)
                    sb.Append($"<text x=\"{x + w - 8:0}\" y=\"{cy:0}\" text-anchor=\"end\" fill=\"#c62828\" font-size=\"11\">{marca}</text>");
                i++;
            }
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    // Punto donde la recta centro-a-centro sale del borde de la caja 'de'
    private static (double, double) Borde(
        (double X, double Y, double W, double H) de,
        (double X, double Y, double W, double H) hacia)
    {
        var cx = de.X + de.W / 2; var cy = de.Y + de.H / 2;
        var dx = hacia.X + hacia.W / 2 - cx; var dy = hacia.Y + hacia.H / 2 - cy;
        if (dx == 0 && dy == 0) return (cx, cy);

        var escala = Math.Min(
            dx == 0 ? double.MaxValue : de.W / 2 / Math.Abs(dx),
            dy == 0 ? double.MaxValue : de.H / 2 / Math.Abs(dy));
        return (cx + dx * escala, cy + dy * escala);
    }

    private static string Etiqueta(double x, double y, double haciaX, double haciaY, string texto)
    {
        var dx = haciaX - x; var dy = haciaY - y;
        var len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        var lx = x + dx / len * 14 - dy / len * 8;
        var ly = y + dy / len * 14 + dx / len * 8;
        return $"<text x=\"{lx:0}\" y=\"{ly + 4:0}\" text-anchor=\"middle\" font-weight=\"bold\" fill=\"#1565c0\">{texto}</text>";
    }
}
