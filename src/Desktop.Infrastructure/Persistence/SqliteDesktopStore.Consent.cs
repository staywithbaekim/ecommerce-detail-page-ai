using System.Globalization;
using Desktop.Application.Ai;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore : IConsentPort
{
    public async Task<AiUploadConsentRecord?> GetConsentAsync(
        string scope,
        CancellationToken cancellationToken)
    {
        ValidateConsentScope(scope);
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT scope, consent_id, state, policy_version, changed_at
            FROM ai_upload_consents
            WHERE scope = $scope;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadConsent(reader);
    }

    public Task<AiUploadConsentRecord> GrantAsync(
        string scope,
        string policyVersion,
        CancellationToken cancellationToken)
    {
        ValidateConsentScope(scope);
        ValidatePolicyVersion(policyVersion);
        var record = new AiUploadConsentRecord(
            scope,
            $"consent-{Guid.NewGuid():N}",
            AiUploadConsentState.Granted,
            policyVersion,
            DateTimeOffset.UtcNow);
        return SaveConsentAsync(record, cancellationToken);
    }

    public async Task<AiUploadConsentRecord> RevokeAsync(
        string scope,
        CancellationToken cancellationToken)
    {
        ValidateConsentScope(scope);
        return await _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var transaction = connection.BeginTransaction(
                    System.Data.IsolationLevel.Serializable,
                    deferred: false);
                var existing = await ReadConsentAsync(connection, transaction, scope, token)
                    .ConfigureAwait(false);
                var record = new AiUploadConsentRecord(
                    scope,
                    existing?.ConsentId ?? $"consent-{Guid.NewGuid():N}",
                    AiUploadConsentState.Revoked,
                    existing?.PolicyVersion ?? AiConsentPolicy.Version,
                    DateTimeOffset.UtcNow);
                await SaveConsentInTransactionAsync(connection, transaction, record, token)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return record;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the final local authorization read at the request admission
    /// boundary. The operation is serialized by the same SQLite writer and
    /// transaction used by <see cref="RevokeAsync"/>; no provider or network
    /// code runs while this transaction is active.
    /// </summary>
    public Task<AiUploadConsentRecord?> TryAcquireAsync(
        string scope,
        CancellationToken cancellationToken)
    {
        ValidateConsentScope(scope);
        return _writer.ExecuteAsync(
            async (connection, token) =>
            {
                // `deferred: false` emits BEGIN IMMEDIATE. Although this
                // operation only reads the row, taking the writer lock here
                // makes the snapshot linearizable with RevokeAsync (which
                // uses the same transaction shape) across store instances.
                await using var transaction = connection.BeginTransaction(
                    System.Data.IsolationLevel.Serializable,
                    deferred: false);
                var record = await ReadConsentAsync(connection, transaction, scope, token)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return record;
            },
            cancellationToken);
    }

    private Task<AiUploadConsentRecord> SaveConsentAsync(
        AiUploadConsentRecord record,
        CancellationToken cancellationToken) =>
        _writer.ExecuteAsync(
            async (connection, token) =>
            {
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(token)
                    .ConfigureAwait(false);
                await SaveConsentInTransactionAsync(connection, transaction, record, token)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return record;
            },
            cancellationToken);

    private static async Task SaveConsentInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AiUploadConsentRecord record,
        CancellationToken cancellationToken)
    {
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                INSERT INTO ai_upload_consents(scope, consent_id, state, policy_version, changed_at)
                VALUES($scope, $consent_id, $state, $policy_version, $changed_at)
                ON CONFLICT(scope) DO UPDATE SET
                    consent_id = excluded.consent_id,
                    state = excluded.state,
                    policy_version = excluded.policy_version,
                    changed_at = excluded.changed_at;
                """;
            AddConsentParameters(current, record);
            await current.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText = """
                INSERT INTO ai_upload_consent_events(
                    event_id, scope, consent_id, state, policy_version, occurred_at)
                VALUES($event_id, $scope, $consent_id, $state, $policy_version, $occurred_at);
                """;
            history.Parameters.AddWithValue("$event_id", $"consent-event-{Guid.NewGuid():N}");
            AddConsentParameters(history, record, includeScope: true, includeState: true, includeChangedAt: false);
            history.Parameters.AddWithValue("$occurred_at", record.ChangedAt.ToString("O"));
            await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<AiUploadConsentRecord?> ReadConsentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scope,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT scope, consent_id, state, policy_version, changed_at
            FROM ai_upload_consents
            WHERE scope = $scope;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadConsent(reader)
            : null;
    }

    private static AiUploadConsentRecord ReadConsent(SqliteDataReader reader)
    {
        var scope = reader.GetString(0);
        var consentId = reader.GetString(1);
        var stateText = reader.GetString(2);
        var policyVersion = reader.GetString(3);
        var changedAt = DateTimeOffset.Parse(
            reader.GetString(4),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        if (!Enum.TryParse<AiUploadConsentState>(stateText, ignoreCase: false, out var state)
            || !Enum.IsDefined(state))
        {
            throw new InvalidDataException("AI_CONSENT_STATE_CORRUPT");
        }

        ValidateConsentScope(scope);
        ValidateOpaqueValue(consentId, "AI_CONSENT_ID_INVALID");
        ValidatePolicyVersion(policyVersion);
        return new(scope, consentId, state, policyVersion, changedAt);
    }

    private static void AddConsentParameters(
        SqliteCommand command,
        AiUploadConsentRecord record,
        bool includeScope = true,
        bool includeState = true,
        bool includeChangedAt = true)
    {
        if (includeScope)
        {
            command.Parameters.AddWithValue("$scope", record.Scope);
        }

        command.Parameters.AddWithValue("$consent_id", record.ConsentId);
        if (includeState)
        {
            command.Parameters.AddWithValue("$state", record.State.ToString());
        }

        command.Parameters.AddWithValue("$policy_version", record.PolicyVersion);
        if (includeChangedAt)
        {
            command.Parameters.AddWithValue("$changed_at", record.ChangedAt.ToString("O"));
        }
    }

    private static void ValidateConsentScope(string scope)
    {
        ValidateOpaqueValue(scope, "AI_CONSENT_SCOPE_INVALID");
    }

    private static void ValidatePolicyVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '/' or '.')))
        {
            throw new InvalidOperationException("AI_CONSENT_POLICY_INVALID");
        }
    }

    private static void ValidateOpaqueValue(string value, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new InvalidOperationException(errorCode);
        }
    }
}
