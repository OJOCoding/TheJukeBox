using Microsoft.Extensions.FileProviders;
using TheJukeBox.API;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();

// Register your services
builder.Services.AddScoped<IGoogleAuthService, GoogleAPI_Service>();
builder.Services.AddScoped<IGoogleDriveService, GoogleDriveService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "JukeBox API",
        Version = "v1",
        Description = "API for streaming Google Drive audio files"
    });
});

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

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "JukeBox API v1");
        c.RoutePrefix = "swagger";
    });
}
else
{
    app.UseExceptionHandler("/error");
}


// Serve ALL static files from CLIENT folder (HTML, CSS, JS, images, etc.)
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(
        Path.Combine(Directory.GetCurrentDirectory(), "CLIENT")),
    RequestPath = "" // This makes files available at root path
});

// Also enable default wwwroot static files (optional)
app.UseCors("AllowAll");
app.UseHttpsRedirection();
app.UseAuthorization();

app.MapControllers();

// Add a simple error handling endpoint
app.Map("/error", () => Results.Problem("An error occurred."));

app.Run();