namespace CarnivalApp.Api.Carnivals;

public static class ValidationRules
{
    // Recorded on every capture/correction audit event: bump on any rule change and
    // never reuse a version string, so the audit trail can say which rules passed a result.
    public const string Version = "coherence-v1";
}
