using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);

DotNetEnv.Env.Load();

var connectionString = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING")
    ?? "Server=localhost;Port=3306;Database=alarms;User=budik_user;Password=a_good_password991838;";

builder.Services.AddScoped(_ => new MySqlConnection(connectionString));

var app = builder.Build();

// ----------------------------------------------------
// 1. GET ALL ALARMS: /api/alarms
// ----------------------------------------------------
app.MapGet("/api/alarms", async (MySqlConnection db) =>
{
    await db.OpenAsync();

    // Fetch core alarms
    using var selectCmd = new MySqlCommand("SELECT id, name, alarm_time, is_enabled, snooze_duration_min, sound_uri FROM alarms;", db);
    using var reader = await selectCmd.ExecuteReaderAsync();

    var alarms = new List<AlarmResponse>();
    while (await reader.ReadAsync())
    {
        alarms.Add(new AlarmResponse
        {
            Id = reader.GetInt32("id"),
            Name = reader.GetString("name"),
            AlarmTime = reader.GetTimeSpan("alarm_time").ToString(@"hh\:mm"),
            IsEnabled = reader.GetBoolean("is_enabled"),
            SnoozeDurationMin = reader.GetInt32("snooze_duration_min"),
            SoundUri = reader.IsDBNull(reader.GetOrdinal("sound_uri")) ? null : reader.GetString("sound_uri")
        });
    }
    await reader.CloseAsync();

    // Attach recurrences and specific dates for each alarm
    foreach (var alarm in alarms)
    {
        // Fetch recurrence rules
        using var recCmd = new MySqlCommand(
            "SELECT day_of_week, interval_weeks, anchor_date, week_parity FROM alarm_recurrence WHERE alarm_id = @id;", db);
        recCmd.Parameters.AddWithValue("@id", alarm.Id);
        
        using var recReader = await recCmd.ExecuteReaderAsync();
        while (await recReader.ReadAsync())
        {
            alarm.Recurrences.Add(new RecurrenceRule(
                DayOfWeek: recReader.GetInt32("day_of_week"),
                IntervalWeeks: recReader.GetInt32("interval_weeks"),
                AnchorDate: recReader.IsDBNull(recReader.GetOrdinal("anchor_date")) ? null : recReader.GetDateTime("anchor_date"),
                WeekParity: recReader.GetString("week_parity")
            ));
        }
        await recReader.CloseAsync();

        // Fetch specific dates
        using var dateCmd = new MySqlCommand("SELECT specific_date FROM alarm_dates WHERE alarm_id = @id;", db);
        dateCmd.Parameters.AddWithValue("@id", alarm.Id);

        using var dateReader = await dateCmd.ExecuteReaderAsync();
        while (await dateReader.ReadAsync())
        {
            alarm.SpecificDates.Add(dateReader.GetDateTime("specific_date").ToString("yyyy-MM-dd"));
        }
        await dateReader.CloseAsync();
    }

    return Results.Ok(alarms);
});

// ----------------------------------------------------
// 2. CREATE ALARM: /api/alarms
// ----------------------------------------------------
app.MapPost("/api/alarms", async (MySqlConnection db, AlarmRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || !TimeSpan.TryParse(request.AlarmTime, out var parsedTime))
    {
        return Results.BadRequest(new { Error = "Valid name and time (HH:mm) are required." });
    }

    await db.OpenAsync();
    await using var transaction = await db.BeginTransactionAsync();

    try
    {
        // Insert Core Alarm
        const string insertSql = @"
            INSERT INTO alarms (name, alarm_time, is_enabled, snooze_duration_min, sound_uri) 
            VALUES (@name, @time, @enabled, @snooze, @sound);
            SELECT LAST_INSERT_ID();";

        using var cmd = new MySqlCommand(insertSql, db, transaction);
        cmd.Parameters.AddWithValue("@name", request.Name);
        cmd.Parameters.AddWithValue("@time", parsedTime);
        cmd.Parameters.AddWithValue("@enabled", request.IsEnabled);
        cmd.Parameters.AddWithValue("@snooze", request.SnoozeDurationMin);
        cmd.Parameters.AddWithValue("@sound", (object?)request.SoundUri ?? DBNull.Value);

        int alarmId = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        // Insert Recurrences
        if (request.Recurrences != null)
        {
            foreach (var rule in request.Recurrences)
            {
                using var recCmd = new MySqlCommand(@"
                    INSERT INTO alarm_recurrence (alarm_id, day_of_week, interval_weeks, anchor_date, week_parity)
                    VALUES (@alarm_id, @day, @interval, @anchor, @parity);", db, transaction);

                recCmd.Parameters.AddWithValue("@alarm_id", alarmId);
                recCmd.Parameters.AddWithValue("@day", rule.DayOfWeek);
                recCmd.Parameters.AddWithValue("@interval", rule.IntervalWeeks);
                recCmd.Parameters.AddWithValue("@anchor", (object?)rule.AnchorDate ?? DBNull.Value);
                recCmd.Parameters.AddWithValue("@parity", rule.WeekParity);
                await recCmd.ExecuteNonQueryAsync();
            }
        }

        // Insert Specific Dates
        if (request.SpecificDates != null)
        {
            foreach (var dateStr in request.SpecificDates)
            {
                if (DateTime.TryParse(dateStr, out var parsedDate))
                {
                    using var dateCmd = new MySqlCommand(@"
                        INSERT INTO alarm_dates (alarm_id, specific_date)
                        VALUES (@alarm_id, @date);", db, transaction);

                    dateCmd.Parameters.AddWithValue("@alarm_id", alarmId);
                    dateCmd.Parameters.AddWithValue("@date", parsedDate.Date);
                    await dateCmd.ExecuteNonQueryAsync();
                }
            }
        }

        await transaction.CommitAsync();
        return Results.Created($"/api/alarms/{alarmId}", new { Message = "Alarm created successfully.", AlarmId = alarmId });
    }
    catch (Exception ex)
    {
        await transaction.RollbackAsync();
        return Results.Problem($"Failed to create alarm: {ex.Message}");
    }
});

// ----------------------------------------------------
// 3. UPDATE ALARM: /api/alarms/{id}
// ----------------------------------------------------
app.MapPut("/api/alarms/{id:int}", async (int id, MySqlConnection db, AlarmRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || !TimeSpan.TryParse(request.AlarmTime, out var parsedTime))
    {
        return Results.BadRequest(new { Error = "Valid name and time (HH:mm) are required." });
    }

    await db.OpenAsync();
    await using var transaction = await db.BeginTransactionAsync();

    try
    {
        // 1. Update Core Alarm
        const string updateSql = @"
            UPDATE alarms 
            SET name = @name, alarm_time = @time, is_enabled = @enabled, snooze_duration_min = @snooze, sound_uri = @sound
            WHERE id = @id;";

        using var cmd = new MySqlCommand(updateSql, db, transaction);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", request.Name);
        cmd.Parameters.AddWithValue("@time", parsedTime);
        cmd.Parameters.AddWithValue("@enabled", request.IsEnabled);
        cmd.Parameters.AddWithValue("@snooze", request.SnoozeDurationMin);
        cmd.Parameters.AddWithValue("@sound", (object?)request.SoundUri ?? DBNull.Value);

        int rows = await cmd.ExecuteNonQueryAsync();
        if (rows == 0)
        {
            await transaction.RollbackAsync();
            return Results.NotFound(new { Message = $"Alarm with ID {id} was not found." });
        }

        // 2. Clear existing child rules
        using var clearRec = new MySqlCommand("DELETE FROM alarm_recurrence WHERE alarm_id = @id;", db, transaction);
        clearRec.Parameters.AddWithValue("@id", id);
        await clearRec.ExecuteNonQueryAsync();

        using var clearDates = new MySqlCommand("DELETE FROM alarm_dates WHERE alarm_id = @id;", db, transaction);
        clearDates.Parameters.AddWithValue("@id", id);
        await clearDates.ExecuteNonQueryAsync();

        // 3. Re-insert Recurrences
        if (request.Recurrences != null)
        {
            foreach (var rule in request.Recurrences)
            {
                using var recCmd = new MySqlCommand(@"
                    INSERT INTO alarm_recurrence (alarm_id, day_of_week, interval_weeks, anchor_date, week_parity)
                    VALUES (@alarm_id, @day, @interval, @anchor, @parity);", db, transaction);

                recCmd.Parameters.AddWithValue("@alarm_id", id);
                recCmd.Parameters.AddWithValue("@day", rule.DayOfWeek);
                recCmd.Parameters.AddWithValue("@interval", rule.IntervalWeeks);
                recCmd.Parameters.AddWithValue("@anchor", (object?)rule.AnchorDate ?? DBNull.Value);
                recCmd.Parameters.AddWithValue("@parity", rule.WeekParity);
                await recCmd.ExecuteNonQueryAsync();
            }
        }

        // 4. Re-insert Specific Dates
        if (request.SpecificDates != null)
        {
            foreach (var dateStr in request.SpecificDates)
            {
                if (DateTime.TryParse(dateStr, out var parsedDate))
                {
                    using var dateCmd = new MySqlCommand(@"
                        INSERT INTO alarm_dates (alarm_id, specific_date)
                        VALUES (@alarm_id, @date);", db, transaction);

                    dateCmd.Parameters.AddWithValue("@alarm_id", id);
                    dateCmd.Parameters.AddWithValue("@date", parsedDate.Date);
                    await dateCmd.ExecuteNonQueryAsync();
                }
            }
        }

        await transaction.CommitAsync();
        return Results.Ok(new { Message = "Alarm updated successfully." });
    }
    catch (Exception ex)
    {
        await transaction.RollbackAsync();
        return Results.Problem($"Failed to update alarm: {ex.Message}");
    }
});

// ----------------------------------------------------
// 4. DELETE ALARM: /api/alarms/{id}
// ----------------------------------------------------
app.MapDelete("/api/alarms/{id:int}", async (int id, MySqlConnection db) =>
{
    await db.OpenAsync();

    // Handled by ON DELETE CASCADE in foreign keys, or manually cleaned here
    const string deleteSql = @"
        DELETE FROM alarm_recurrence WHERE alarm_id = @id;
        DELETE FROM alarm_dates WHERE alarm_id = @id;
        DELETE FROM alarms WHERE id = @id;";

    using var cmd = new MySqlCommand(deleteSql, db);
    cmd.Parameters.AddWithValue("@id", id);

    int rowsAffected = await cmd.ExecuteNonQueryAsync();
    if (rowsAffected == 0)
    {
        return Results.NotFound(new { Message = $"Alarm with ID {id} was not found." });
    }

    return Results.NoContent();
});

app.Run();

// ----------------------------------------------------
// DATA MODELS (DTOs)
// ----------------------------------------------------
public record RecurrenceRule(
    int DayOfWeek, 
    int IntervalWeeks = 1, 
    DateTime? AnchorDate = null, 
    string WeekParity = "all"
);

public record AlarmRequest(
    string Name,
    string AlarmTime,
    bool IsEnabled = true,
    int SnoozeDurationMin = 5,
    string? SoundUri = null,
    List<RecurrenceRule>? Recurrences = null,
    List<string>? SpecificDates = null
);

public class AlarmResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AlarmTime { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int SnoozeDurationMin { get; set; }
    public string? SoundUri { get; set; }
    public List<RecurrenceRule> Recurrences { get; set; } = new();
    public List<string> SpecificDates { get; set; } = new();
}
