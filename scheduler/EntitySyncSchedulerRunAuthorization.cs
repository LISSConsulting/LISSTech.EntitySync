using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace LISSTech.EntitySync.Scheduler;

internal sealed class EntitySyncSchedulerRunAuthorization
{
    internal const string EnvironmentVariable = "SCHEDULER_RUN_TOKEN";
    private const int MinimumTokenLength = 32;
    private const int MaximumTokenLength = 256;
    private readonly byte[] expectedDigest;

    private EntitySyncSchedulerRunAuthorization(string token)
    {
        if (string.IsNullOrWhiteSpace(token)
            || token.Length < MinimumTokenLength
            || token.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} must contain at least {MinimumTokenLength} characters and no whitespace.");
        }
        if (token.Length > MaximumTokenLength)
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} must contain no more than {MaximumTokenLength} characters.");
        }

        expectedDigest = Hash(token);
    }

    internal static EntitySyncSchedulerRunAuthorization FromCurrentEnvironment() =>
        new(Environment.GetEnvironmentVariable(EnvironmentVariable) ?? string.Empty);

    internal bool IsAuthorized(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers.Authorization.Count != 1
            || !AuthenticationHeaderValue.TryParse(request.Headers.Authorization[0], out var authorization)
            || !authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(authorization.Parameter)
            || authorization.Parameter.Length > MaximumTokenLength)
        {
            return false;
        }

        var actualDigest = Hash(authorization.Parameter);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualDigest);
        }
    }

    private static byte[] Hash(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> bytes = stackalloc byte[byteCount];
        Encoding.UTF8.GetBytes(value, bytes);
        var digest = SHA256.HashData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return digest;
    }
}
