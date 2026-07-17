var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// GET
app.MapGet("/", () => "Hello World!");

app.MapGet("/hi", () => "hi");

app.Run();
