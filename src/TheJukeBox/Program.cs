using Scalar.AspNetCore;
using TheJukeBox.API;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddScoped<IGoogleAuthService, GoogleAPI_Service>();
builder.Services.AddScoped<IGoogleDriveService, GoogleDriveService>();

builder.Services.AddOpenApi();

// TODO(A1): delete this entirely. The frontend is served same-origin from this
// host, so no cross-origin access is needed -- and AllowAnyOrigin combined with
// the server-side Drive token lets any site the user visits read their library.
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.MapOpenApi();
    app.MapScalarApiReference();
}
else
{
    app.UseExceptionHandler("/error");
}

// UseDefaultFiles rewrites "/" to "/index.html" so UseStaticFiles can serve it.
// Order matters: the rewrite must run before the file handler.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("AllowAll");
app.UseHttpsRedirection();

app.MapControllers();

app.MapGet("/player", (IWebHostEnvironment env) =>
    TypedResults.PhysicalFile(Path.Combine(env.WebRootPath, "player.html"), "text/html"));

app.Map("/error", () => Results.Problem("An error occurred."));

app.Run();
