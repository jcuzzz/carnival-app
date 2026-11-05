namespace CarnivalApp.Api.School;

/// <summary>Who may hold a local account beside an identity provider; with none, everyone signs in locally.</summary>
public enum LocalAccountAccess
{
    SystemAdministrators,
    Everyone,
}
