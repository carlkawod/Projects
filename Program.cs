using Microsoft.Data.Sqlite;
using OfficeOpenXml;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

builder.Services.AddAntiforgery();

var app = builder.Build();

// join all pages index.html, student.html, styles.css, etc.
app.UseStaticFiles();
app.UseAntiforgery();

// import Excel
ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

// main
app.MapGet("/", () => Results.Redirect("/index.html"));


// ==========================================================
// Setup Database
// ==========================================================
var connectionString = "Data Source=AssesmentReportGenerator.db";

// Table already exists in AssesmentReportGenerator.db — no need to create it



// ==========================================================
// Search Student
// ==========================================================
app.MapGet("/search", (string? lastName, string? firstName, string? id, string? semester, string? gradYear) =>
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    var sql = @"
        SELECT StudentID, FirstName, LastName, Year, ExpectedGradYear
        FROM Student
        WHERE 1 = 1
    ";

    if (!string.IsNullOrWhiteSpace(lastName))
        sql += " AND LastName LIKE @last";

    if (!string.IsNullOrWhiteSpace(firstName))
        sql += " AND FirstName LIKE @first";

    if (!string.IsNullOrWhiteSpace(id))
        sql += " AND CAST(StudentID AS TEXT) LIKE @id";

    if (!string.IsNullOrWhiteSpace(semester))
        sql += " AND Year LIKE @semester";

    if (!string.IsNullOrWhiteSpace(gradYear))
        sql += " AND CAST(ExpectedGradYear AS TEXT) LIKE @gradYear";

    using var command = connection.CreateCommand();
    command.CommandText = sql;

    if (!string.IsNullOrWhiteSpace(lastName))
        command.Parameters.AddWithValue("@last", $"%{lastName}%");

    if (!string.IsNullOrWhiteSpace(firstName))
        command.Parameters.AddWithValue("@first", $"%{firstName}%");

    if (!string.IsNullOrWhiteSpace(id))
        command.Parameters.AddWithValue("@id", $"%{id}%");

    if (!string.IsNullOrWhiteSpace(semester))
        command.Parameters.AddWithValue("@semester", $"%{semester}%");

    if (!string.IsNullOrWhiteSpace(gradYear))
        command.Parameters.AddWithValue("@gradYear", $"%{gradYear}%");

    var results = new List<object>();

    using var reader = command.ExecuteReader();

    while (reader.Read())
    {
        results.Add(new
        {
            studentID = reader["StudentID"]?.ToString(),
            firstName = reader["FirstName"]?.ToString(),
            lastName = reader["LastName"]?.ToString(),
            email = "",
            expGradTerm = reader["ExpectedGradYear"]?.ToString(),
            major = reader["Year"]?.ToString()
        });
    }

    return Results.Json(results);
});


// ==========================================================
// JUST ONE STUDENT
// ==========================================================
app.MapGet("/student/{studentId}", (string studentId) =>
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    var sql = @"
        SELECT StudentID, FirstName, LastName, Year, ExpectedGradYear
        FROM Student
        WHERE CAST(StudentID AS TEXT) = @studentId
    ";

    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue("@studentId", studentId);

    using var reader = command.ExecuteReader();

    if (!reader.Read())
    {
        return Results.NotFound(new { message = "Student not found." });
    }

    var student = new
    {
        studentID = reader["StudentID"]?.ToString(),
        firstName = reader["FirstName"]?.ToString(),
        lastName = reader["LastName"]?.ToString(),
        email = "",
        expGradTerm = reader["ExpectedGradYear"]?.ToString(),
        major = reader["Year"]?.ToString(),
        status = "Active"
    };

    return Results.Json(student);
});


// ==========================================================
// CREATE STUDENT
// ==========================================================
app.MapPost("/create", async (HttpContext http) =>
{
    var student = await http.Request.ReadFromJsonAsync<StudentDto>();

    if (student == null ||
        string.IsNullOrWhiteSpace(student.StudentID) ||
        string.IsNullOrWhiteSpace(student.FirstName) ||
        string.IsNullOrWhiteSpace(student.LastName))
    {
        return Results.BadRequest("StudentID, FirstName, and LastName are required.");
    }

    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = @"
        INSERT INTO Student (StudentID, FirstName, LastName, Year, ExpectedGradYear)
        VALUES (@id, @first, @last, @year, @gradYear)";

    command.Parameters.AddWithValue("@id", student.StudentID);
    command.Parameters.AddWithValue("@first", student.FirstName);
    command.Parameters.AddWithValue("@last", student.LastName);
    command.Parameters.AddWithValue("@year", student.Major ?? "");
    command.Parameters.AddWithValue("@gradYear", student.ExpGradTerm ?? "");

    command.ExecuteNonQuery();

    return Results.Ok("Student created.");
});


// ==========================================================
// EDIT STUDENT
// ==========================================================
app.MapPut("/edit", async (HttpContext http) =>
{
    var student = await http.Request.ReadFromJsonAsync<StudentDto>();

    if (student == null || string.IsNullOrWhiteSpace(student.StudentID))
    {
        return Results.BadRequest("StudentID is required.");
    }

    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = @"
        UPDATE Student
        SET FirstName = @first,
            LastName = @last,
            Year = @year,
            ExpectedGradYear = @gradYear
        WHERE CAST(StudentID AS TEXT) = @id";

    command.Parameters.AddWithValue("@id", student.StudentID);
    command.Parameters.AddWithValue("@first", student.FirstName ?? "");
    command.Parameters.AddWithValue("@last", student.LastName ?? "");
    command.Parameters.AddWithValue("@year", student.Major ?? "");
    command.Parameters.AddWithValue("@gradYear", student.ExpGradTerm ?? "");

    var rows = command.ExecuteNonQuery();

    return rows == 0
        ? Results.NotFound("No student found with that ID.")
        : Results.Ok("Student updated.");
});


// ==========================================================
// REMOVE STUDENT
// ==========================================================
app.MapDelete("/delete", (string? id) =>
{
    if (string.IsNullOrWhiteSpace(id))
    {
        return Results.BadRequest("StudentID is required.");
    }

    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    try
    {
        var deleteAssignments = connection.CreateCommand();
        deleteAssignments.CommandText = "DELETE FROM Assignment WHERE CAST(StudentID AS TEXT) = @id";
        deleteAssignments.Parameters.AddWithValue("@id", id);
        deleteAssignments.ExecuteNonQuery();
    }
    catch
    {
        // Ignore if no assignment
    }

    var deleteStudent = connection.CreateCommand();
    deleteStudent.CommandText = "DELETE FROM Student WHERE CAST(StudentID AS TEXT) = @id";
    deleteStudent.Parameters.AddWithValue("@id", id);

    var rows = deleteStudent.ExecuteNonQuery();

    return rows == 0
        ? Results.NotFound("No student found with that ID.")
        : Results.Ok("Student removed.");
});


// ==========================================================
// IMPORT STUDENT
// ==========================================================
app.MapPost("/import", async (IFormFile file) =>
{
    if (file == null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();

    if (ext != ".csv" && ext != ".xlsx")
        return Results.BadRequest("Only .csv and .xlsx files are supported.");

    var students = new List<StudentDto>();

    if (ext == ".csv")
    {
        using var reader = new StreamReader(file.OpenReadStream());

        await reader.ReadLineAsync();

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var cols = line.Split(',');

            if (cols.Length < 6)
                continue;

            students.Add(new StudentDto
            {
                StudentID = cols[0].Trim(),
                FirstName = cols[1].Trim(),
                LastName = cols[2].Trim(),
                Email = cols[3].Trim(),
                ExpGradTerm = cols[4].Trim(),
                Major = cols[5].Trim()
            });
        }
    }
    else
    {
        using var stream = file.OpenReadStream();
        using var package = new ExcelPackage(stream);

        var worksheet = package.Workbook.Worksheets[0];
        var rowCount = worksheet.Dimension?.Rows ?? 0;

        for (int row = 2; row <= rowCount; row++)
        {
            var studentID = worksheet.Cells[row, 1].Text.Trim();

            if (string.IsNullOrWhiteSpace(studentID))
                continue;

            students.Add(new StudentDto
            {
                StudentID = studentID,
                FirstName = worksheet.Cells[row, 2].Text.Trim(),
                LastName = worksheet.Cells[row, 3].Text.Trim(),
                Email = worksheet.Cells[row, 4].Text.Trim(),
                ExpGradTerm = worksheet.Cells[row, 5].Text.Trim(),
                Major = worksheet.Cells[row, 6].Text.Trim()
            });
        }
    }

    if (students.Count == 0)
        return Results.BadRequest("No valid rows found in the file.");

    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    int inserted = 0;

    foreach (var s in students)
    {
        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR IGNORE INTO Student (StudentID, FirstName, LastName, Year, ExpectedGradYear)
            VALUES (@id, @first, @last, @year, @gradYear)";

        command.Parameters.AddWithValue("@id", s.StudentID);
        command.Parameters.AddWithValue("@first", s.FirstName);
        command.Parameters.AddWithValue("@last", s.LastName);
        command.Parameters.AddWithValue("@year", s.Major ?? "");
        command.Parameters.AddWithValue("@gradYear", s.ExpGradTerm ?? "");

        inserted += command.ExecuteNonQuery();
    }

    return Results.Json(new
    {
        message = $"Import complete. {inserted} student(s) added."
    });
}).DisableAntiforgery();

app.Run();

record StudentDto
{
    public string? StudentID { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Email { get; init; }
    public string? ExpGradTerm { get; init; }
    public string? Major { get; init; }
}