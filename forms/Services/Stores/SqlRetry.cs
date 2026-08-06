using Microsoft.Data.SqlClient;

namespace forms.Services;

/// <summary>
/// Shared transient-fault retry policy for the SQL Server stores, built on
/// Microsoft.Data.SqlClient's own configurable retry logic — no extra dependency.
/// Exponential back-off over an explicit list of transient SQL error numbers:
/// deadlock victims, connection resets, login/network timeouts, and Azure SQL
/// throttling/failover (40501, 40613, 49918-49920, 10928/10929, …).
///
/// The provider is attached to every connection's
/// <see cref="SqlConnection.RetryLogicProvider"/>, so a transient failure to
/// <c>OpenAsync</c> — where the large majority of transient faults surface — is
/// retried transparently. Read commands attach it to the command as well; writes
/// deliberately do not, so a transient fault can never replay a non-idempotent
/// INSERT/UPDATE/DELETE (a duplicate-key or double-apply hazard). This mirrors the
/// boundary EF Core's execution strategy draws for the same reason.
/// </summary>
internal static class SqlRetry
{
    // Explicit rather than relying on the driver's default set, so the policy is
    // visible and reviewable. Sourced from the well-known Azure SQL / SQL Server
    // transient error numbers (the same list EF Core's detector uses).
    private static readonly int[] TransientErrorNumbers =
    [
        1204, 1205, 1222, 3935, 3960, 4060, 4221, 10053, 10054, 10060,
        10928, 10929, 10936, 40197, 40143, 40501, 40540, 40613,
        49918, 49919, 49920, 233, 121, 64, 20,
    ];

    public static readonly SqlRetryLogicBaseProvider Provider =
        SqlConfigurableRetryFactory.CreateExponentialRetryProvider(new SqlRetryLogicOption
        {
            // NumberOfTries counts the first attempt, so this is one try + four retries.
            NumberOfTries = 5,
            DeltaTime = TimeSpan.FromSeconds(1),
            MaxTimeInterval = TimeSpan.FromSeconds(20),
            TransientErrors = TransientErrorNumbers,
        });

    /// <summary>
    /// Open a fresh pooled connection with transient-fault retry applied to the
    /// open itself. The caller owns disposal (use <c>await using</c>).
    /// </summary>
    public static async Task<SqlConnection> OpenConnectionAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString) { RetryLogicProvider = Provider };
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            // Opening failed after retries — don't leak the half-constructed
            // connection back to the caller, who never got a chance to dispose it.
            await connection.DisposeAsync();
            throw;
        }
    }
}
