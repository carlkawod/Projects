using Microsoft.Data.Sqlite;
using OfficeOpenXml;
using System.Text.Json;
//
//
var builder = WebApplication.CreateBuilder(args);

// Make JSON deserialization case-insensitive so "studentID" from JS maps to "StudentID" in the DTO
builder.Services.ConfigureHttpJsonOptions(options => {
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

app.UseStaticFiles();

// EPPlus license context (required for v5+; NonCommercial is free for academic/personal use)
ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

app.MapGet("/", () => Results.Redirect("/index.html"));


// Login Endpoint
app.MapPost("/api/login", (LoginRequest request) =>
{
    // In a real app, check a database and compare password hashes here.
    if (request.Username == "user1" && request.Password == "123")
    {
        // Success! Send back a 200 OK status and a token.
        return Results.Ok(new { message = "Login successful", token = "fake-jwt-token-123" });
    }
    
    // Failure! Send back a 401 Unauthorized status.
    return Results.Unauthorized();
});


// ── Search ───────────────────────────────────────────────────────────────────
app.MapGet("/search", (string? lastName, string? firstName, string? id) =>
{
    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var sql = "SELECT * FROM Student WHERE 1=1";
    if (!string.IsNullOrEmpty(lastName))   sql += " AND LastName LIKE @last";
    if (!string.IsNullOrEmpty(firstName))  sql += " AND FirstName LIKE @first";
    if (!string.IsNullOrEmpty(id))         sql += " AND StudentID LIKE @id";

    var command = connection.CreateCommand();
    command.CommandText = sql;
    if (!string.IsNullOrEmpty(lastName))   command.Parameters.AddWithValue("@last",  $"%{lastName}%");
    if (!string.IsNullOrEmpty(firstName))  command.Parameters.AddWithValue("@first", $"%{firstName}%");
    if (!string.IsNullOrEmpty(id))         command.Parameters.AddWithValue("@id",    $"%{id}%");

    var results = new List<Dictionary<string, object>>();
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
        var row = new Dictionary<string, object>();
        for (int i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.GetValue(i);
        results.Add(row);
    }

    return Results.Json(results);
});


// ── Create Student ────────────────────────────────────────────────────────────
// Receives a JSON body from the frontend and inserts one row into Student.
app.MapPost("/create", async (HttpContext http) =>
{
    // Read and deserialize the JSON body
    var student = await http.Request.ReadFromJsonAsync<StudentDto>();

    if (student == null ||
        string.IsNullOrWhiteSpace(student.StudentID) ||
        string.IsNullOrWhiteSpace(student.FirstName) ||
        string.IsNullOrWhiteSpace(student.LastName))
    {
        return Results.BadRequest("StudentID, FirstName, and LastName are required.");
    }

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = @"
        INSERT INTO Student (StudentID, FirstName, LastName, Year, ExpectedGradYear)
        VALUES (@id, @first, @last, @year, @grad)";
    command.Parameters.AddWithValue("@id",    student.StudentID);
    command.Parameters.AddWithValue("@first", student.FirstName);
    command.Parameters.AddWithValue("@last",  student.LastName);
    command.Parameters.AddWithValue("@year",  student.Year ?? "");
    command.Parameters.AddWithValue("@grad",  student.ExpectedGradYear ?? "");

    command.ExecuteNonQuery();
    return Results.Ok("Student created.");
});


// ── Import Students ───────────────────────────────────────────────────────────
// Accepts a multipart file upload (.csv or .xlsx) and bulk-inserts rows.
app.MapPost("/import", async (IFormFile file) =>
{
    if (file == null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();

    if (ext != ".csv" && ext != ".xlsx")
        return Results.BadRequest("Only .csv and .xlsx files are supported.");

    // Parse rows into a list of StudentDto objects
    var students = new List<StudentDto>();

    if (ext == ".csv")
    {
        using var reader = new StreamReader(file.OpenReadStream());

        // Skip the header row
        var header = await reader.ReadLineAsync();

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = line.Split(',');

            // Expect at least 5 columns: StudentID, FirstName, LastName, Year, ExpectedGradYear
            if (cols.Length < 5) continue;

            students.Add(new StudentDto
            {
                StudentID        = cols[0].Trim(),
                FirstName        = cols[1].Trim(),
                LastName         = cols[2].Trim(),
                Year             = cols[3].Trim(),
                ExpectedGradYear = cols[4].Trim()
            });
        }
    }
    else // .xlsx
    {
        using var stream = file.OpenReadStream();
        using var package = new ExcelPackage(stream);

        // Use the first worksheet in the workbook
        var worksheet = package.Workbook.Worksheets[0];
        var rowCount  = worksheet.Dimension?.Rows ?? 0;

        // Start at row 2 to skip the header row
        for (int row = 2; row <= rowCount; row++)
        {
            var studentID = worksheet.Cells[row, 1].Text.Trim();
            if (string.IsNullOrEmpty(studentID)) continue;

            students.Add(new StudentDto
            {
                StudentID        = studentID,
                FirstName        = worksheet.Cells[row, 2].Text.Trim(),
                LastName         = worksheet.Cells[row, 3].Text.Trim(),
                Year             = worksheet.Cells[row, 4].Text.Trim(),
                ExpectedGradYear = worksheet.Cells[row, 5].Text.Trim()
            });
        }
    }

    if (students.Count == 0)
        return Results.BadRequest("No valid rows found in the file.");

    // Bulk insert all parsed rows
    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    int inserted = 0;
    foreach (var s in students)
    {
        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR IGNORE INTO Student (StudentID, FirstName, LastName, Year, ExpectedGradYear)
            VALUES (@id, @first, @last, @year, @grad)";
        command.Parameters.AddWithValue("@id",    s.StudentID);
        command.Parameters.AddWithValue("@first", s.FirstName);
        command.Parameters.AddWithValue("@last",  s.LastName);
        command.Parameters.AddWithValue("@year",  s.Year ?? "");
        command.Parameters.AddWithValue("@grad",  s.ExpectedGradYear ?? "");
        inserted += command.ExecuteNonQuery();
    }

    return Results.Json(new { message = $"Import complete. {inserted} student(s) added." });
})
.DisableAntiforgery();


// ── Edit Student ──────────────────────────────────────────────────────────────
// Receives a JSON body and updates the matching Student row by StudentID.
app.MapPut("/edit", async (HttpContext http) =>
{
    var student = await http.Request.ReadFromJsonAsync<StudentDto>();

    if (student == null || string.IsNullOrWhiteSpace(student.StudentID))
        return Results.BadRequest("StudentID is required.");

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = @"
        UPDATE Student
        SET FirstName = @first, LastName = @last, Year = @year, ExpectedGradYear = @grad
        WHERE StudentID = @id";
    command.Parameters.AddWithValue("@id",    student.StudentID);
    command.Parameters.AddWithValue("@first", student.FirstName ?? "");
    command.Parameters.AddWithValue("@last",  student.LastName ?? "");
    command.Parameters.AddWithValue("@year",  student.Year ?? "");
    command.Parameters.AddWithValue("@grad",  student.ExpectedGradYear ?? "");

    var rows = command.ExecuteNonQuery();

    return rows == 0
        ? Results.NotFound("No student found with that ID.")
        : Results.Ok("Student updated.");
});


// ── Delete Student ────────────────────────────────────────────────────────────
// Removes the student and any related rows by StudentID.
app.MapDelete("/delete", (string? id) =>
{
    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest("StudentID is required.");

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var deleteStudentAssignments = connection.CreateCommand();
    deleteStudentAssignments.CommandText = "DELETE FROM StudentAssignment WHERE StudentID = @id";
    deleteStudentAssignments.Parameters.AddWithValue("@id", id);
    deleteStudentAssignments.ExecuteNonQuery();

    var deleteStudent = connection.CreateCommand();
    deleteStudent.CommandText = "DELETE FROM Student WHERE StudentID = @id";
    deleteStudent.Parameters.AddWithValue("@id", id);

    var rows = deleteStudent.ExecuteNonQuery();

    return rows == 0
        ? Results.NotFound("No student found with that ID.")
        : Results.Ok("Student removed.");
});

// ── Get Courses for a Student ────────────────────────────────────────────────
app.MapGet("/student-courses", (string sid) =>
{
    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = @"
        SELECT DISTINCT c.CourseID, c.CourseName
        FROM StudentAssignment sa
        INNER JOIN Assignment a ON sa.AssignmentID = a.AssignmentID
        INNER JOIN Course c ON a.CourseID = c.CourseID
        WHERE sa.StudentID = @sid
        ORDER BY c.CourseName";
    command.Parameters.AddWithValue("@sid", sid);

    using var reader = command.ExecuteReader();
    var courses = new List<object>();

    while (reader.Read())
    {
        courses.Add(new
        {
            courseId = reader["CourseID"]?.ToString() ?? "",
            courseName = reader["CourseName"]?.ToString() ?? ""
        });
    }

    return Results.Ok(courses);
});

// ── Add Assignment ────────────────────────────────────────────────────────────
app.MapPost("/add-assignment", async (HttpContext http) =>
{
    var asgn = await http.Request.ReadFromJsonAsync<AssignmentDto>();

    if (asgn == null || string.IsNullOrWhiteSpace(asgn.AssignmentName))
        return Results.BadRequest("Assignment name is required.");

    if (string.IsNullOrWhiteSpace(asgn.StudentID))
        return Results.BadRequest("StudentID is required.");

    if (string.IsNullOrWhiteSpace(asgn.CourseID))
        return Results.BadRequest("CourseID is required.");

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    // 1. Insert into Assignment
    var insertAssignment = connection.CreateCommand();
    insertAssignment.CommandText = @"
        INSERT INTO Assignment (AssignmentType, AssignmentName, PLO1, PLO2, PLO3, PLO4, Comments, CourseID)
        VALUES (@type, @name, @plo1, @plo2, @plo3, @plo4, @comments, @courseId)";
    
    insertAssignment.Parameters.AddWithValue("@type", asgn.AssignmentType ?? "");
    insertAssignment.Parameters.AddWithValue("@name", asgn.AssignmentName);
    insertAssignment.Parameters.AddWithValue("@plo1", asgn.PLO1 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@plo2", asgn.PLO2 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@plo3", asgn.PLO3 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@plo4", asgn.PLO4 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@comments", asgn.Comments ?? "");
    insertAssignment.Parameters.AddWithValue("@courseId", asgn.CourseID);

    insertAssignment.ExecuteNonQuery();

    // 2. Get the new AssignmentID
    var getIdCommand = connection.CreateCommand();
    getIdCommand.CommandText = "SELECT last_insert_rowid()";
    var newAssignmentId = Convert.ToInt32(getIdCommand.ExecuteScalar());

    // 3. Link it to the student in StudentAssignment
    var linkCommand = connection.CreateCommand();
    linkCommand.CommandText = @"
        INSERT INTO StudentAssignment (StudentID, AssignmentID)
        VALUES (@sid, @aid)";
    linkCommand.Parameters.AddWithValue("@sid", asgn.StudentID);
    linkCommand.Parameters.AddWithValue("@aid", newAssignmentId);
    linkCommand.ExecuteNonQuery();

    return Results.Ok("Assignment added.");
});

// ── Get Assignments for a Student and Course ────────────────────────────────
app.MapGet("/assignments", (string sid, string? courseId) =>
{
    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = @"
        SELECT a.AssignmentName, a.PLO1, a.PLO2, a.PLO3, a.PLO4
        FROM StudentAssignment sa
        INNER JOIN Assignment a ON sa.AssignmentID = a.AssignmentID
        WHERE sa.StudentID = @sid
          AND (@courseId IS NULL OR @courseId = '' OR a.CourseID = @courseId)
        ORDER BY a.AssignmentName";
    command.Parameters.AddWithValue("@sid", sid);
    command.Parameters.AddWithValue("@courseId", courseId ?? "");

    using var reader = command.ExecuteReader();
    var assignments = new List<object>();

    while (reader.Read())
    {
        assignments.Add(new {
            assignmentName = reader["AssignmentName"] != DBNull.Value ? reader["AssignmentName"].ToString() : "Unnamed",
            plo1 = reader["PLO1"] != DBNull.Value && Convert.ToInt32(reader["PLO1"]) == 1,
            plo2 = reader["PLO2"] != DBNull.Value && Convert.ToInt32(reader["PLO2"]) == 1,
            plo3 = reader["PLO3"] != DBNull.Value && Convert.ToInt32(reader["PLO3"]) == 1,
            plo4 = reader["PLO4"] != DBNull.Value && Convert.ToInt32(reader["PLO4"]) == 1
        });
    }

    return Results.Ok(assignments);
});

app.Run();

// Simple class used for both /create and /import to hold student fields.
// Property names must match the JSON keys sent from the frontend.
public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public record AssignmentDto(
    string StudentID, 
    string CourseID,
    string AssignmentType, 
    string AssignmentName, 
    bool PLO1, 
    bool PLO2, 
    bool PLO3, 
    bool PLO4, 
    string Comments
);

record StudentDto
{
    public string? StudentID        { get; init; }
    public string? FirstName        { get; init; }
    public string? LastName         { get; init; }
    public string? Year             { get; init; }
    public string? ExpectedGradYear { get; init; }
}
