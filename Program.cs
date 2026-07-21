using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);

// Load the environment variables from the .env file
DotNetEnv.Env.Load();

// Retrieve the connection string from the environment, falling back if missing
var connectionString = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING")
    ?? "Server=localhost;Port=3306;Database=mydb;User=budik_user;Password=a_good_password991838;";

builder.Services.AddScoped(_ => new MySqlConnection(connectionString));

var app = builder.Build();

// GET
app.MapGet("/", () => "Hello World!");
app.MapGet("/hi", () => "hi");

// Example: test the DB connection
app.MapGet("/db-check", async (MySqlConnection db) =>
{
    await db.OpenAsync();
    using var cmd = new MySqlCommand("SELECT VERSION()", db);
    var version = await cmd.ExecuteScalarAsync();
    return Results.Ok(new { MySqlVersion = version });
});

app.MapGet("/get-alarms", async (MySqlConnection db) =>
{
    await db.OpenAsync();

    using var selectCmd = new MySqlCommand(
        @"SELECT a.id, a.name, a.alarm_time, a.is_enabled, 
                 GROUP_CONCAT(d.day_of_week SEPARATOR ' ') AS selected_days
          FROM alarms a
          LEFT JOIN alarm_days d ON a.id = d.alarm_id
          GROUP BY a.id", 
        db
    );

    using var reader = await selectCmd.ExecuteReaderAsync();
    var alarmsList = new List<object>();

    while (await reader.ReadAsync())
    {
        alarmsList.Add(new 
        {
            Id = reader.GetInt32("id"),
            Name = reader.GetString("name"),
            AlarmTime = reader.GetString("alarm_time"),
            IsEnabled = reader.GetInt32("is_enabled") == 1,
            Days = reader.IsDBNull(reader.GetOrdinal("selected_days")) 
                ? "" 
                : reader.GetString("selected_days")
        });
    }

    if (alarmsList.Count > 0)
    {
        return Results.Ok(alarmsList);
    }

    return Results.Problem("No data found during test select.");
});




app.Run();
