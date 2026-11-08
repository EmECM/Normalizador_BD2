using Normalizador.Core.Servicios;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

builder.Services.AddScoped<AnalizadorPrerrequisitos>();
builder.Services.AddScoped<ServicioConexion>();
builder.Services.AddScoped<CorrectorPrerrequisitos>();
builder.Services.AddScoped<Normalizador1FN>();
builder.Services.AddScoped<Normalizador2FN>();
builder.Services.AddScoped<Normalizador3FN>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.Run();
