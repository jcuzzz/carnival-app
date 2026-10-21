using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Imports;

public enum RowDisposition
{
    New,
    Changed,
    Unchanged,
    /// <summary>
    /// Non-active source status; retained as an inactive identity reference only.
    /// </summary>
    Inactive,
    Rejected,
}

public enum IssueSeverity
{
    Warning,
    /// <summary>Rejects the row.</summary>
    Error,
    /// <summary>
    /// Rejects the row, but is expected operator work to resolve at source, not bad data.
    /// </summary>
    Exception,
}

public sealed record RowIssue(string Code, IssueSeverity Severity, string Message);

public sealed record RowValidation(int RowNumber, string? ExternalStudentId, IReadOnlyList<RowIssue> Issues);

public sealed record ValidationDocument(
    IReadOnlyList<string> StructuralErrors, IReadOnlyList<RowValidation> RowIssues);

/// <summary>Derived competition fields only — never the source birthdate.</summary>
public sealed class StagedRow
{
    public int RowNumber { get; set; }
    public string? ExternalStudentId { get; set; }
    public string? Surname { get; set; }
    public string? FirstName { get; set; }
    public string? PreferredName { get; set; }
    public string? EnrolmentStatus { get; set; }
    public string? HouseCode { get; set; }
    public string? HomeGroup { get; set; }
    public int? YearLevel { get; set; }
    public string? CompetitionAgeGroup { get; set; }
    public CompetitionCategory? CompetitionCategory { get; set; }
    public RowDisposition Disposition { get; set; }
    public List<RowIssue> Issues { get; set; } = [];

    public bool IsRejected => Disposition == RowDisposition.Rejected;
}

public sealed record UnmatchedStudent(string ExternalStudentId, string Surname, string FirstName);

public sealed class StagedBatchDocument
{
    public int CompetitionYear { get; set; }

    /// <summary>MM-DD. Absent on documents staged before the cut-off was recorded.</summary>
    public string? CutOff { get; set; }
    public List<StagedRow> Rows { get; set; } = [];
    public List<UnmatchedStudent> UnmatchedStudents { get; set; } = [];
}

public sealed record ReconciliationSummary(
    int New, int Changed, int Unchanged, int Inactive, int Unmatched, int Rejected);
