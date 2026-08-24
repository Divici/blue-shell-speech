namespace Practice.Domain.Common;

/// <summary>
/// Argument checks for domain invariants.
///
/// These throw rather than returning a result because a Provider with a blank display
/// name is not a validation failure to be reported to a user — it is a bug. Input from
/// people is validated at the API boundary; by the time a domain constructor runs, the
/// values are expected to be sound.
/// </summary>
public static class Guard
{
    public static string NotBlank(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.", name);
        }

        return value.Trim();
    }

    public static string MaxLength(string value, int max, string name)
    {
        if (value.Length > max)
        {
            throw new ArgumentException($"{name} must be {max} characters or fewer.", name);
        }

        return value;
    }
}
