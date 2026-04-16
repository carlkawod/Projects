using Microsoft.Data.Sqlite;
using OfficeOpenXml;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options => {
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

app.UseStaticFiles();

ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

// ── DB Migrations ─────────────────────────────────────────────────────────────
using (var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db"))
{
    connection.Open();

    // Add Email and Major columns to Student if missing
    var checkStudentCols = connection.CreateCommand();
    checkStudentCols.CommandText = "PRAGMA table_info(Student)";
    bool hasEmailCol = false, hasMajorStudentCol = false;
    using (var r = checkStudentCols.ExecuteReader())
        while (r.Read())
        {
            var colName = r["name"].ToString();
            if (colName == "Email") hasEmailCol = true;
            if (colName == "Major") hasMajorStudentCol = true;
        }
    if (!hasEmailCol)
    {
        var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Student ADD COLUMN Email TEXT";
        alter.ExecuteNonQuery();
    }
    if (!hasMajorStudentCol)
    {
        var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Student ADD COLUMN Major TEXT";
        alter.ExecuteNonQuery();
    }

    // Add SemesterID to Assignment if missing
    var checkSem = connection.CreateCommand();
    checkSem.CommandText = "PRAGMA table_info(Assignment)";
    bool hasSemesterCol = false;
    using (var r = checkSem.ExecuteReader())
        while (r.Read())
            if (r["name"].ToString() == "SemesterID") { hasSemesterCol = true; break; }
    if (!hasSemesterCol)
    {
        var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Assignment ADD COLUMN SemesterID INTEGER";
        alter.ExecuteNonQuery();
    }

    // Add Semesters column to Course if missing
    var checkSemesters = connection.CreateCommand();
    checkSemesters.CommandText = "PRAGMA table_info(Course)";
    bool hasSemestersCol = false;
    using (var r = checkSemesters.ExecuteReader())
        while (r.Read())
            if (r["name"].ToString() == "Semesters") { hasSemestersCol = true; break; }
    if (!hasSemestersCol)
    {
        var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Course ADD COLUMN Semesters TEXT";
        alter.ExecuteNonQuery();
    }

    // Add Major column to Course if missing
    var checkMajor = connection.CreateCommand();
    checkMajor.CommandText = "PRAGMA table_info(Course)";
    bool hasMajorCol = false;
    using (var r = checkMajor.ExecuteReader())
        while (r.Read())
            if (r["name"].ToString() == "Major") { hasMajorCol = true; break; }
    if (!hasMajorCol)
    {
        var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Course ADD COLUMN Major TEXT";
        alter.ExecuteNonQuery();

        // Default existing CSC courses to Computer Science
        var update = connection.CreateCommand();
        update.CommandText = "UPDATE Course SET Major = 'Computer Science' WHERE CourseID LIKE 'CSC%'";
        update.ExecuteNonQuery();
    }

    // Add Grade to StudentAssignment if missing
    var checkGrade = connection.CreateCommand();
    checkGrade.CommandText = "PRAGMA table_info(StudentAssignment)";
    bool hasGradeCol = false;
    using (var r = checkGrade.ExecuteReader())
        while (r.Read())
            if (r["name"].ToString() == "Grade") { hasGradeCol = true; break; }
    if (!hasGradeCol)
    {
        var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE StudentAssignment ADD COLUMN Grade INTEGER";
        alter.ExecuteNonQuery();
    }

    // Seed semesters if empty
    var countCmd = connection.CreateCommand();
    countCmd.CommandText = "SELECT COUNT(*) FROM Semester";
    if (Convert.ToInt32(countCmd.ExecuteScalar()) == 0)
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
}

app.MapGet("/", () => Results.Redirect("/index.html"));


// ── Login ─────────────────────────────────────────────────────────────────────
app.MapPost("/api/login", (LoginRequest request) =>
{
    if (request.Username == "user1" && request.Password == "123")
        return Results.Ok(new { message = "Login successful", token = "fake-jwt-token-123" });
    return Results.Unauthorized();
});


// ── Search ────────────────────────────────────────────────────────────────────
app.MapGet("/search", (string? lastName, string? firstName, string? id, string? major) =>
{
    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var sql = "SELECT * FROM Student WHERE 1=1";
    if (!string.IsNullOrEmpty(lastName))  sql += " AND LastName LIKE @last";
    if (!string.IsNullOrEmpty(firstName)) sql += " AND FirstName LIKE @first";
    if (!string.IsNullOrEmpty(id))        sql += " AND StudentID LIKE @id";
    if (!string.IsNullOrEmpty(major))     sql += " AND Major = @major";

    var command = connection.CreateCommand();
    command.CommandText = sql;
    if (!string.IsNullOrEmpty(lastName))  command.Parameters.AddWithValue("@last",  $"%{lastName}%");
    if (!string.IsNullOrEmpty(firstName)) command.Parameters.AddWithValue("@first", $"%{firstName}%");
    if (!string.IsNullOrEmpty(id))        command.Parameters.AddWithValue("@id",    $"%{id}%");
    if (!string.IsNullOrEmpty(major))     command.Parameters.AddWithValue("@major", major);

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
app.MapPost("/create", async (HttpContext http) =>
{
    var student = await http.Request.ReadFromJsonAsync<StudentDto>();

    if (student == null ||
        string.IsNullOrWhiteSpace(student.StudentID) ||
        string.IsNullOrWhiteSpace(student.FirstName) ||
        string.IsNullOrWhiteSpace(student.LastName))
        return Results.BadRequest("StudentID, FirstName, and LastName are required.");

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = @"
        INSERT INTO Student (StudentID, FirstName, LastName, Year, ExpectedGradYear, Email, Major)
        VALUES (@id, @first, @last, @year, @grad, @email, @major)";
    command.Parameters.AddWithValue("@id",    student.StudentID);
    command.Parameters.AddWithValue("@first", student.FirstName);
    command.Parameters.AddWithValue("@last",  student.LastName);
    command.Parameters.AddWithValue("@year",  student.Year ?? "");
    command.Parameters.AddWithValue("@grad",  student.ExpectedGradYear ?? "");
    command.Parameters.AddWithValue("@email", student.Email ?? "");
    command.Parameters.AddWithValue("@major", student.Major ?? "");
    command.ExecuteNonQuery();

    return Results.Ok("Student created.");
});


// ── Import Students ───────────────────────────────────────────────────────────
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
        await reader.ReadLineAsync(); // skip header
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split(',');
            if (cols.Length < 5) continue;
            students.Add(new StudentDto {
                StudentID        = cols[0].Trim(),
                FirstName        = cols[1].Trim(),
                LastName         = cols[2].Trim(),
                Year             = cols[3].Trim(),
                ExpectedGradYear = cols[4].Trim()
            });
        }
    }
    else
    {
        using var stream = file.OpenReadStream();
        using var package = new ExcelPackage(stream);
        var ws = package.Workbook.Worksheets[0];
        var rowCount = ws.Dimension?.Rows ?? 0;
        for (int row = 2; row <= rowCount; row++)
        {
            var sid = ws.Cells[row, 1].Text.Trim();
            if (string.IsNullOrEmpty(sid)) continue;
            students.Add(new StudentDto {
                StudentID        = sid,
                FirstName        = ws.Cells[row, 2].Text.Trim(),
                LastName         = ws.Cells[row, 3].Text.Trim(),
                Year             = ws.Cells[row, 4].Text.Trim(),
                ExpectedGradYear = ws.Cells[row, 5].Text.Trim()
            });
        }
    }

    if (students.Count == 0)
        return Results.BadRequest("No valid rows found in the file.");

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
        SET FirstName = @first, LastName = @last, Year = @year, ExpectedGradYear = @grad, Email = @email, Major = @major
        WHERE StudentID = @id";
    command.Parameters.AddWithValue("@id",    student.StudentID);
    command.Parameters.AddWithValue("@first", student.FirstName ?? "");
    command.Parameters.AddWithValue("@last",  student.LastName ?? "");
    command.Parameters.AddWithValue("@year",  student.Year ?? "");
    command.Parameters.AddWithValue("@grad",  student.ExpectedGradYear ?? "");
    command.Parameters.AddWithValue("@email", student.Email ?? "");
    command.Parameters.AddWithValue("@major", student.Major ?? "");

    var rows = command.ExecuteNonQuery();
    return rows == 0
        ? Results.NotFound("No student found with that ID.")
        : Results.Ok("Student updated.");
});


// ── Delete Student ────────────────────────────────────────────────────────────
app.MapDelete("/delete", (string? id) =>
{
    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest("StudentID is required.");

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var delAsgn = connection.CreateCommand();
    delAsgn.CommandText = "DELETE FROM StudentAssignment WHERE StudentID = @id";
    delAsgn.Parameters.AddWithValue("@id", id);
    delAsgn.ExecuteNonQuery();

    var delStudent = connection.CreateCommand();
    delStudent.CommandText = "DELETE FROM Student WHERE StudentID = @id";
    delStudent.Parameters.AddWithValue("@id", id);
    var rows = delStudent.ExecuteNonQuery();

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
    command.CommandText = "SELECT CourseID, CourseName, Credits, Major, Semesters FROM Course ORDER BY CourseName";

    using var reader = command.ExecuteReader();
    var courses = new List<object>();
    while (reader.Read())
        courses.Add(new {
            courseId   = reader["CourseID"]?.ToString()   ?? "",
            courseName = reader["CourseName"]?.ToString() ?? "",
            credits    = reader["Credits"]  != DBNull.Value ? (int?)Convert.ToInt32(reader["Credits"]) : null,
            major      = reader["Major"]?.ToString()     ?? "",
            semesters  = reader["Semesters"]?.ToString() ?? ""
        });

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
        semesters.Add(new { semesterId = reader["SemesterID"]?.ToString() ?? "", semesterName = reader["SemesterName"]?.ToString() ?? "" });

    return Results.Ok(semesters);
});


// ── Create Course ─────────────────────────────────────────────────────────────
app.MapPost("/create-course", async (HttpContext http) =>
{
    var course = await http.Request.ReadFromJsonAsync<CourseDto>();

    if (course == null || string.IsNullOrWhiteSpace(course.CourseID) || string.IsNullOrWhiteSpace(course.CourseName))
        return Results.BadRequest("CourseID and CourseName are required.");

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = "INSERT INTO Course (CourseID, CourseName, Credits, Major, Semesters) VALUES (@id, @name, @credits, @major, @semesters)";
    command.Parameters.AddWithValue("@id",       course.CourseID);
    command.Parameters.AddWithValue("@name",     course.CourseName);
    command.Parameters.AddWithValue("@credits",  course.Credits ?? 3);
    command.Parameters.AddWithValue("@major",    course.Major ?? "");
    command.Parameters.AddWithValue("@semesters", course.Semesters ?? "");
    command.ExecuteNonQuery();

    return Results.Ok("Course created.");
});


// ── Enroll Student in Course ──────────────────────────────────────────────────
app.MapPost("/enroll", async (HttpContext http) =>
{
    var dto = await http.Request.ReadFromJsonAsync<EnrollmentDto>();

    if (dto == null || string.IsNullOrWhiteSpace(dto.StudentID) || string.IsNullOrWhiteSpace(dto.CourseID))
        return Results.BadRequest("StudentID and CourseID are required.");

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var check = connection.CreateCommand();
    check.CommandText = "SELECT COUNT(*) FROM Enrollment WHERE StudentID = @sid AND CourseID = @cid";
    check.Parameters.AddWithValue("@sid", dto.StudentID);
    check.Parameters.AddWithValue("@cid", dto.CourseID);
    if (Convert.ToInt32(check.ExecuteScalar()) > 0)
        return Results.Conflict("Student is already enrolled in this course.");

    var cmd = connection.CreateCommand();
    cmd.CommandText = "INSERT INTO Enrollment (StudentID, CourseID, SemesterID) VALUES (@sid, @cid, @semId)";
    cmd.Parameters.AddWithValue("@sid",   dto.StudentID);
    cmd.Parameters.AddWithValue("@cid",   dto.CourseID);
    cmd.Parameters.AddWithValue("@semId", string.IsNullOrWhiteSpace(dto.SemesterID) ? DBNull.Value : (object)dto.SemesterID);
    cmd.ExecuteNonQuery();

    // Auto-link all existing assignments for this course to the student
    var getAssignments = connection.CreateCommand();
    getAssignments.CommandText = "SELECT AssignmentID FROM Assignment WHERE CourseID = @cid";
    getAssignments.Parameters.AddWithValue("@cid", dto.CourseID);

    var assignmentIds = new List<int>();
    using (var r = getAssignments.ExecuteReader())
        while (r.Read())
            assignmentIds.Add(Convert.ToInt32(r["AssignmentID"]));

    foreach (var aid in assignmentIds)
    {
        // Only link if not already linked
        var alreadyLinked = connection.CreateCommand();
        alreadyLinked.CommandText = "SELECT COUNT(*) FROM StudentAssignment WHERE StudentID = @sid AND AssignmentID = @aid";
        alreadyLinked.Parameters.AddWithValue("@sid", dto.StudentID);
        alreadyLinked.Parameters.AddWithValue("@aid", aid);
        if (Convert.ToInt32(alreadyLinked.ExecuteScalar()) > 0) continue;

        var link = connection.CreateCommand();
        link.CommandText = "INSERT INTO StudentAssignment (StudentID, AssignmentID) VALUES (@sid, @aid)";
        link.Parameters.AddWithValue("@sid", dto.StudentID);
        link.Parameters.AddWithValue("@aid", aid);
        link.ExecuteNonQuery();
    }

    return Results.Ok("Enrolled successfully.");
});


// ── Get Courses for a Student ─────────────────────────────────────────────────
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
        courses.Add(new { courseId = reader["CourseID"]?.ToString() ?? "", courseName = reader["CourseName"]?.ToString() ?? "" });

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

    var insertAssignment = connection.CreateCommand();
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

    insertAssignment.Parameters.AddWithValue("@type",       asgn.AssignmentType ?? "");
    insertAssignment.Parameters.AddWithValue("@name",       asgn.AssignmentName);
    insertAssignment.Parameters.AddWithValue("@courseId",   asgn.CourseID);
    insertAssignment.Parameters.AddWithValue("@semesterId", string.IsNullOrWhiteSpace(asgn.SemesterID) ? DBNull.Value : (object)asgn.SemesterID);
    insertAssignment.Parameters.AddWithValue("@comments",   asgn.Comments ?? "");
    insertAssignment.Parameters.AddWithValue("@plo1", asgn.PLO1 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@plo2", asgn.PLO2 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@plo3", asgn.PLO3 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@plo4", asgn.PLO4 ? 1 : 0);
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

    var getIdCmd = connection.CreateCommand();
    getIdCmd.CommandText = "SELECT last_insert_rowid()";
    var newAssignmentId = Convert.ToInt32(getIdCmd.ExecuteScalar());

    var linkCmd = connection.CreateCommand();
    linkCmd.CommandText = "INSERT INTO StudentAssignment (StudentID, AssignmentID) VALUES (@sid, @aid)";
    linkCmd.Parameters.AddWithValue("@sid", asgn.StudentID);
    linkCmd.Parameters.AddWithValue("@aid", newAssignmentId);
    linkCmd.ExecuteNonQuery();

    return Results.Ok("Assignment added.");
});


// ── Get Assignments ───────────────────────────────────────────────────────────
app.MapGet("/assignments", (string sid, string? courseId, string? semesterId) =>
{
    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = @"
        SELECT sa.StudentAssignmentID, a.AssignmentName, a.PLO1, a.PLO2, a.PLO3, a.PLO4, sa.Grade
        FROM StudentAssignment sa
        INNER JOIN Assignment a ON sa.AssignmentID = a.AssignmentID
        WHERE sa.StudentID = @sid
          AND (@courseId   = '' OR a.CourseID   = @courseId)
          AND (@semesterId = '' OR a.SemesterID = @semesterId)
        ORDER BY a.AssignmentName";
    command.Parameters.AddWithValue("@sid",        sid);
    command.Parameters.AddWithValue("@courseId",   courseId   ?? "");
    command.Parameters.AddWithValue("@semesterId", semesterId ?? "");

    using var reader = command.ExecuteReader();
    var assignments = new List<object>();
    while (reader.Read())
    {
        assignments.Add(new {
            studentAssignmentId = Convert.ToInt32(reader["StudentAssignmentID"]),
            assignmentName      = reader["AssignmentName"]?.ToString() ?? "Unnamed",
            plo1  = reader["PLO1"]  != DBNull.Value && Convert.ToInt32(reader["PLO1"])  == 1,
            plo2  = reader["PLO2"]  != DBNull.Value && Convert.ToInt32(reader["PLO2"])  == 1,
            plo3  = reader["PLO3"]  != DBNull.Value && Convert.ToInt32(reader["PLO3"])  == 1,
            plo4  = reader["PLO4"]  != DBNull.Value && Convert.ToInt32(reader["PLO4"])  == 1,
            grade = reader["Grade"] != DBNull.Value ? (int?)Convert.ToInt32(reader["Grade"]) : null
        });
    }
    return Results.Ok(assignments);
});


// ── Update Grade ──────────────────────────────────────────────────────────────
app.MapPut("/update-grade", async (HttpContext http) =>
{
    var dto = await http.Request.ReadFromJsonAsync<GradeDto>();

    if (dto == null || dto.StudentAssignmentID <= 0)
        return Results.BadRequest("StudentAssignmentID is required.");

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = "UPDATE StudentAssignment SET Grade = @grade WHERE StudentAssignmentID = @id";
    command.Parameters.AddWithValue("@grade", dto.Grade.HasValue ? (object)dto.Grade.Value : DBNull.Value);
    command.Parameters.AddWithValue("@id",    dto.StudentAssignmentID);
    command.ExecuteNonQuery();

    return Results.Ok("Grade updated.");
});


// ── Edit Course ───────────────────────────────────────────────────────────────
app.MapPut("/edit-course", async (HttpContext http) =>
{
    var course = await http.Request.ReadFromJsonAsync<CourseDto>();

    if (course == null || string.IsNullOrWhiteSpace(course.CourseID))
        return Results.BadRequest("CourseID is required.");

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = @"
        UPDATE Course SET CourseName = @name, Credits = @credits, Major = @major, Semesters = @semesters
        WHERE CourseID = @id";
    command.Parameters.AddWithValue("@id",        course.CourseID);
    command.Parameters.AddWithValue("@name",      course.CourseName ?? "");
    command.Parameters.AddWithValue("@credits",   course.Credits ?? 3);
    command.Parameters.AddWithValue("@major",     course.Major ?? "");
    command.Parameters.AddWithValue("@semesters", course.Semesters ?? "");

    var rows = command.ExecuteNonQuery();
    return rows == 0
        ? Results.NotFound("Course not found.")
        : Results.Ok("Course updated.");
});


// ── Delete Course ─────────────────────────────────────────────────────────────
app.MapDelete("/delete-course", (string? id) =>
{
    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest("CourseID is required.");

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    // Remove enrollments for this course
    var delEnroll = connection.CreateCommand();
    delEnroll.CommandText = "DELETE FROM Enrollment WHERE CourseID = @id";
    delEnroll.Parameters.AddWithValue("@id", id);
    delEnroll.ExecuteNonQuery();

    var delCourse = connection.CreateCommand();
    delCourse.CommandText = "DELETE FROM Course WHERE CourseID = @id";
    delCourse.Parameters.AddWithValue("@id", id);
    var rows = delCourse.ExecuteNonQuery();

    return rows == 0
        ? Results.NotFound("Course not found.")
        : Results.Ok("Course deleted.");
});


// ── Get All Students ──────────────────────────────────────────────────────────
app.MapGet("/students", () =>
{
    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = "SELECT StudentID, FirstName, LastName, Major FROM Student ORDER BY LastName, FirstName";

    using var reader = command.ExecuteReader();
    var students = new List<object>();
    while (reader.Read())
    {
        students.Add(new {
            studentId  = reader["StudentID"]?.ToString() ?? "",
            firstName  = reader["FirstName"]?.ToString() ?? "",
            lastName   = reader["LastName"]?.ToString()  ?? "",
            major      = reader["Major"]?.ToString()     ?? ""
        });
    }
    return Results.Ok(students);
});


// ── Add Assignment (Course level — no student required) ───────────────────────
app.MapPost("/add-assignment-course", async (HttpContext http) =>
{
    var asgn = await http.Request.ReadFromJsonAsync<AssignmentDto>();

    if (asgn == null || string.IsNullOrWhiteSpace(asgn.AssignmentName))
        return Results.BadRequest("Assignment name is required.");
    if (string.IsNullOrWhiteSpace(asgn.CourseID))
        return Results.BadRequest("CourseID is required.");

    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var insertAssignment = connection.CreateCommand();
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

    insertAssignment.Parameters.AddWithValue("@type",       asgn.AssignmentType ?? "");
    insertAssignment.Parameters.AddWithValue("@name",       asgn.AssignmentName);
    insertAssignment.Parameters.AddWithValue("@courseId",   asgn.CourseID);
    insertAssignment.Parameters.AddWithValue("@semesterId", string.IsNullOrWhiteSpace(asgn.SemesterID) ? DBNull.Value : (object)asgn.SemesterID);
    insertAssignment.Parameters.AddWithValue("@comments",   asgn.Comments ?? "");
    insertAssignment.Parameters.AddWithValue("@plo1", asgn.PLO1 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@plo2", asgn.PLO2 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@plo3", asgn.PLO3 ? 1 : 0);
    insertAssignment.Parameters.AddWithValue("@plo4", asgn.PLO4 ? 1 : 0);
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

    return Results.Ok("Assignment added.");
});


// ── Get Students Enrolled in a Course ────────────────────────────────────────
app.MapGet("/course-students", (string courseId) =>
{
    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = @"
        SELECT s.StudentID, s.FirstName, s.LastName, s.Major, s.ExpectedGradYear
        FROM Enrollment e
        INNER JOIN Student s ON e.StudentID = s.StudentID
        WHERE e.CourseID = @cid
        ORDER BY s.LastName, s.FirstName";
    command.Parameters.AddWithValue("@cid", courseId);

    using var reader = command.ExecuteReader();
    var students = new List<object>();
    while (reader.Read())
    {
        students.Add(new {
            studentId       = reader["StudentID"]?.ToString()       ?? "",
            firstName       = reader["FirstName"]?.ToString()       ?? "",
            lastName        = reader["LastName"]?.ToString()        ?? "",
            major           = reader["Major"]?.ToString()           ?? "",
            expectedGradYear = reader["ExpectedGradYear"]?.ToString() ?? ""
        });
    }
    return Results.Ok(students);
});


// ── Get Enrolled Students for a Course ───────────────────────────────────────
app.MapGet("/course-enrollments", (string courseId) =>
{
    using var connection = new SqliteConnection("Data Source=AssesmentReportGenerator.db");
    connection.Open();

    var command = connection.CreateCommand();
    command.CommandText = @"
        SELECT DISTINCT e.StudentID
        FROM Enrollment e
        WHERE e.CourseID = @cid";
    command.Parameters.AddWithValue("@cid", courseId);

    using var reader = command.ExecuteReader();
    var ids = new List<string>();
    while (reader.Read())
        ids.Add(reader["StudentID"]?.ToString() ?? "");

    return Results.Ok(ids);
});


app.Run();


// ── DTOs ──────────────────────────────────────────────────────────────────────
public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public record AssignmentDto(
    string  StudentID,
    string  CourseID,
    string? SemesterID,
    string  AssignmentType,
    string  AssignmentName,
    bool PLO1, bool PLO2, bool PLO3, bool PLO4,
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
    public string? Email            { get; init; }
    public string? Major            { get; init; }
}

record CourseDto
{
    public string? CourseID   { get; init; }
    public string? CourseName { get; init; }
    public int?    Credits    { get; init; }
    public string? Major      { get; init; }
    public string? Semesters  { get; init; }
}

record EnrollmentDto
{
    public string? StudentID  { get; init; }
    public string? CourseID   { get; init; }
    public string? SemesterID { get; init; }
}

record GradeDto
{
    public int  StudentAssignmentID { get; init; }
    public int? Grade               { get; init; }
}
