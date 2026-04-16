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

// ── DB Migrations ─────────────────────────────────────────────────────────────
using (var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db"))
{
    connection.Open();

    // Add SemesterID column to Assignment if it doesn't exist yet
    var checkCol = connection.CreateCommand();
    checkCol.CommandText = "PRAGMA table_info(Assignment)";
    bool hasSemester = false;
    using (var r = checkCol.ExecuteReader())
        while (r.Read())
            if (r["name"].ToString() == "SemesterID") { hasSemester = true; break; }

    if (!hasSemester)
    {
        var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Assignment ADD COLUMN SemesterID INTEGER";
        alter.ExecuteNonQuery();
    }

    // Seed default semesters if table is empty
    var countCmd = connection.CreateCommand();
    countCmd.CommandText = "SELECT COUNT(*) FROM Semester";
    var count = Convert.ToInt32(countCmd.ExecuteScalar());
    if (count == 0)
    {
        var seeds = new[] {
            "Fall 2023", "Spring 2024", "Summer 2024",
            "Fall 2024", "Spring 2025", "Summer 2025",
            "Fall 2025", "Spring 2026"
        };
        foreach (var name in seeds)
        {
            var ins = connection.CreateCommand();
            ins.CommandText = "INSERT INTO Semester (SemesterName) VALUES (@name)";
            ins.Parameters.AddWithValue("@name", name);
            ins.ExecuteNonQuery();
        }
    }

    // Create AssignmentGrade table if it doesn't exist
    var createGrade = connection.CreateCommand();
    createGrade.CommandText = @"
        CREATE TABLE IF NOT EXISTS AssignmentGrade (
            GradeID      INTEGER PRIMARY KEY AUTOINCREMENT,
            AssignmentID INTEGER NOT NULL,
            StudentID    TEXT    NOT NULL,
            PLO          INTEGER NOT NULL,
            RawScore     REAL    NOT NULL,
            GradeLevel   INTEGER NOT NULL
        )";
    createGrade.ExecuteNonQuery();
}


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

// ── Get All Courses ───────────────────────────────────────────────────────────
app.MapGet("/courses", () =>
{
    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = "SELECT CourseID, CourseName FROM Course ORDER BY CourseName";

    using var reader = command.ExecuteReader();
    var courses = new List<object>();

    while (reader.Read())
    {
        courses.Add(new
        {
            courseId   = reader["CourseID"]?.ToString()   ?? "",
            courseName = reader["CourseName"]?.ToString() ?? ""
        });
    }

    return Results.Ok(courses);
});

// ── Get All Semesters ─────────────────────────────────────────────────────────
app.MapGet("/semesters", () =>
{
    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = "SELECT SemesterID, SemesterName FROM Semester ORDER BY SemesterID";

    using var reader = command.ExecuteReader();
    var semesters = new List<object>();

    while (reader.Read())
    {
        semesters.Add(new
        {
            semesterId   = reader["SemesterID"]?.ToString()   ?? "",
            semesterName = reader["SemesterName"]?.ToString() ?? ""
        });
    }

    return Results.Ok(semesters);
});

// ── Create Course ─────────────────────────────────────────────────────────────
app.MapPost("/create-course", async (HttpContext http) =>
{
    var course = await http.Request.ReadFromJsonAsync<CourseDto>();

    if (course == null ||
        string.IsNullOrWhiteSpace(course.CourseID) ||
        string.IsNullOrWhiteSpace(course.CourseName))
    {
        return Results.BadRequest("CourseID and CourseName are required.");
    }

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = @"
        INSERT INTO Course (CourseID, CourseName, Credits)
        VALUES (@id, @name, @credits)";
    command.Parameters.AddWithValue("@id",      course.CourseID);
    command.Parameters.AddWithValue("@name",    course.CourseName);
    command.Parameters.AddWithValue("@credits", course.Credits ?? 3);

    command.ExecuteNonQuery();
    return Results.Ok("Course created.");
});

// ── Get Courses for a Student ────────────────────────────────────────────────
app.MapGet("/student-courses", (string sid) =>
{
    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = @"
        SELECT c.CourseID, c.CourseName
        FROM Enrollment e
        INNER JOIN Course c ON e.CourseID = c.CourseID
        WHERE e.StudentID = @sid
        UNION
        SELECT DISTINCT c.CourseID, c.CourseName
        FROM StudentAssignment sa
        INNER JOIN Assignment a ON sa.AssignmentID = a.AssignmentID
        INNER JOIN Course c ON a.CourseID = c.CourseID
        WHERE sa.StudentID = @sid
        ORDER BY CourseName";
    command.Parameters.AddWithValue("@sid", sid);

    using var reader = command.ExecuteReader();
    var courses = new List<object>();

    while (reader.Read())
    {
        courses.Add(new
        {
            courseId   = reader["CourseID"]?.ToString()   ?? "",
            courseName = reader["CourseName"]?.ToString() ?? ""
        });
    }

    return Results.Ok(courses);
});

// ── Enroll Student in Course ──────────────────────────────────────────────────
app.MapPost("/enroll", async (HttpContext http) =>
{
    var dto = await http.Request.ReadFromJsonAsync<EnrollmentDto>();

    if (dto == null || string.IsNullOrWhiteSpace(dto.StudentID) || string.IsNullOrWhiteSpace(dto.CourseID))
        return Results.BadRequest("StudentID and CourseID are required.");

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    // Prevent duplicate enrollment
    var check = connection.CreateCommand();
    check.CommandText = @"
        SELECT COUNT(*) FROM Enrollment
        WHERE StudentID = @sid AND CourseID = @cid
          AND (@semId IS NULL OR SemesterID = @semId)";
    check.Parameters.AddWithValue("@sid",   dto.StudentID);
    check.Parameters.AddWithValue("@cid",   dto.CourseID);
    check.Parameters.AddWithValue("@semId", string.IsNullOrWhiteSpace(dto.SemesterID) ? DBNull.Value : (object)dto.SemesterID);

    if (Convert.ToInt32(check.ExecuteScalar()) > 0)
        return Results.Conflict("Student is already enrolled in this course.");

    var cmd = connection.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO Enrollment (StudentID, CourseID, SemesterID)
        VALUES (@sid, @cid, @semId)";
    cmd.Parameters.AddWithValue("@sid",   dto.StudentID);
    cmd.Parameters.AddWithValue("@cid",   dto.CourseID);
    cmd.Parameters.AddWithValue("@semId", string.IsNullOrWhiteSpace(dto.SemesterID) ? DBNull.Value : (object)dto.SemesterID);
    cmd.ExecuteNonQuery();

    return Results.Ok("Enrolled successfully.");
});

// ── Save Assignment Grades ────────────────────────────────────────────────────
app.MapPost("/save-grades", async (HttpContext http) =>
{
    var dto = await http.Request.ReadFromJsonAsync<SaveGradesDto>();

    if (dto == null || dto.AssignmentID <= 0 || string.IsNullOrWhiteSpace(dto.StudentID))
        return Results.BadRequest("AssignmentID and StudentID are required.");

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    foreach (var grade in dto.Grades)
    {
        int level = grade.RawScore >= 90 ? 4 :
                    grade.RawScore >= 80 ? 3 :
                    grade.RawScore >= 70 ? 2 : 1;

        // Upsert — delete existing then insert
        var del = connection.CreateCommand();
        del.CommandText = "DELETE FROM AssignmentGrade WHERE AssignmentID=@aid AND StudentID=@sid AND PLO=@plo";
        del.Parameters.AddWithValue("@aid", dto.AssignmentID);
        del.Parameters.AddWithValue("@sid", dto.StudentID);
        del.Parameters.AddWithValue("@plo", grade.PLO);
        del.ExecuteNonQuery();

        var ins = connection.CreateCommand();
        ins.CommandText = @"
            INSERT INTO AssignmentGrade (AssignmentID, StudentID, PLO, RawScore, GradeLevel)
            VALUES (@aid, @sid, @plo, @raw, @lvl)";
        ins.Parameters.AddWithValue("@aid", dto.AssignmentID);
        ins.Parameters.AddWithValue("@sid", dto.StudentID);
        ins.Parameters.AddWithValue("@plo", grade.PLO);
        ins.Parameters.AddWithValue("@raw", grade.RawScore);
        ins.Parameters.AddWithValue("@lvl", level);
        ins.ExecuteNonQuery();
    }

    return Results.Ok("Grades saved.");
});

// ── Get Grades for an Assignment ──────────────────────────────────────────────
app.MapGet("/assignment-grades", (int assignmentId, string sid) =>
{
    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var cmd = connection.CreateCommand();
    cmd.CommandText = @"
        SELECT PLO, RawScore, GradeLevel
        FROM AssignmentGrade
        WHERE AssignmentID = @aid AND StudentID = @sid";
    cmd.Parameters.AddWithValue("@aid", assignmentId);
    cmd.Parameters.AddWithValue("@sid", sid);

    using var reader = cmd.ExecuteReader();
    var grades = new List<object>();
    while (reader.Read())
    {
        grades.Add(new {
            plo        = Convert.ToInt32(reader["PLO"]),
            rawScore   = Convert.ToDouble(reader["RawScore"]),
            gradeLevel = Convert.ToInt32(reader["GradeLevel"])
        });
    }

    return Results.Ok(grades);
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
    
    // UPDATE: Added the 10 metric columns to the INSERT statement
    insertAssignment.CommandText = @"
        INSERT INTO Assignment (
            AssignmentType, AssignmentName, CourseID, SemesterID, Comments, 
            PLO1, PLO2, PLO3, PLO4,
            plo1_1, plo1_2, plo1_3, plo1_4,
            plo2_1, plo2_2,
            plo3_1, plo3_2, plo3_3,
            plo4_1
        )
        VALUES (
            @type, @name, @courseId, @semesterId, @comments, 
            @plo1, @plo2, @plo3, @plo4,
            @m1_1, @m1_2, @m1_3, @m1_4,
            @m2_1, @m2_2,
            @m3_1, @m3_2, @m3_3,
            @m4_1
        )";
    
    insertAssignment.Parameters.AddWithValue("@type", asgn.AssignmentType ?? "");
    insertAssignment.Parameters.AddWithValue("@name", asgn.AssignmentName);
    insertAssignment.Parameters.AddWithValue("@courseId", asgn.CourseID);
    insertAssignment.Parameters.AddWithValue("@semesterId", string.IsNullOrWhiteSpace(asgn.SemesterID) ? DBNull.Value : (object)asgn.SemesterID);
    insertAssignment.Parameters.AddWithValue("@comments", asgn.Comments ?? "");
    
    // Main PLOs
    insertAssignment.Parameters.AddWithValue("@plo1", asgn.PLO1 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@plo2", asgn.PLO2 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@plo3", asgn.PLO3 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@plo4", asgn.PLO4 ? 1 : 0);

    // ADDED: Bind the metric parameters
    insertAssignment.Parameters.AddWithValue("@m1_1", asgn.plo1_1 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@m1_2", asgn.plo1_2 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@m1_3", asgn.plo1_3 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@m1_4", asgn.plo1_4 ? 1 : 0);
    
    insertAssignment.Parameters.AddWithValue("@m2_1", asgn.plo2_1 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@m2_2", asgn.plo2_2 ? 1 : 0);
    
    insertAssignment.Parameters.AddWithValue("@m3_1", asgn.plo3_1 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@m3_2", asgn.plo3_2 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@m3_3", asgn.plo3_3 ? 1 : 0);
    
    insertAssignment.Parameters.AddWithValue("@m4_1", asgn.plo4_1 ? 1 : 0);

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
app.MapGet("/assignments", (string sid, string? courseId, string? semesterId) =>
{
    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = @"
        SELECT a.AssignmentID, a.AssignmentName, a.PLO1, a.PLO2, a.PLO3, a.PLO4
        FROM StudentAssignment sa
        INNER JOIN Assignment a ON sa.AssignmentID = a.AssignmentID
        WHERE sa.StudentID = @sid
          AND (@courseId IS NULL OR @courseId = '' OR a.CourseID = @courseId)
          AND (@semesterId IS NULL OR @semesterId = '' OR a.SemesterID = @semesterId)
        ORDER BY a.AssignmentName";
    command.Parameters.AddWithValue("@sid", sid);
    command.Parameters.AddWithValue("@courseId", courseId ?? "");
    command.Parameters.AddWithValue("@semesterId", semesterId ?? "");

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
    string? SemesterID,
    string AssignmentType, 
    string AssignmentName, 
    bool PLO1, 
    bool PLO2, 
    bool PLO3, 
    bool PLO4, 
    
    // ADDED: The 10 metric variables matching the frontend IDs
    bool plo1_1, bool plo1_2, bool plo1_3, bool plo1_4,
    bool plo2_1, bool plo2_2,
    bool plo3_1, bool plo3_2, bool plo3_3,
    bool plo4_1,

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

record CourseDto
{
    public string? CourseID   { get; init; }
    public string? CourseName { get; init; }
    public int?    Credits    { get; init; }
}
