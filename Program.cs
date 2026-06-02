var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "videotool is running on Azure App Service.");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
