using System.Text.Json;
using ElsaControl.Provisioning.Abstractions;
using Microsoft.Data.Sqlite;

namespace ElsaControl.Provisioning.Core.Stores;

/// <summary>
/// Module-owned SQLite tables for the provisioning queue, events, requests,
/// and stack locks. Used when <see cref="ProvisioningOptions.StorePath"/> is
/// set. Tests keep the in-memory store.
/// </summary>
public sealed class SqliteProvisioningStore :
    IProvisioningOperationStore,
    IProvisioningEventStore,
    IProvisioningStackLockStore,
    IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public SqliteProvisioningStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path.Trim());
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        _connection.Open();
        using var pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        EnsureSchema();
    }

    public Task<ProvisioningEnqueueResult> CreateOrGetAsync(
        ProvisioningRequest request,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        lock (_gate)
        {
            var stackName = ProvisioningStackName.Canonicalize(request.Target.StackName);
            var lockKey = ProvisioningStackLockKey.Format(request.Target.StackKind, stackName);
            var lockHeld = LockHeld(lockKey) || HasLockHoldingOperation(lockKey);
            var lockConflict = lockHeld && request.Command != ProvisioningCommand.Cancel;
            var stateNow = lockConflict
                ? ProvisioningOperationState.LockConflict
                : ProvisioningOperationState.Queued;
            var operation = new ProvisioningOperation(
                Guid.NewGuid(),
                request.Command,
                stateNow,
                request.Target.ProviderId,
                request.Target.StackKind,
                stackName,
                request.IdempotencyKey,
                requestHash,
                now,
                completedAt: lockConflict ? now : null,
                failureCode: lockConflict ? ProvisioningFailureCodes.LockConflict : null);

            if (!TryInsertOperation(operation, request))
                return Task.FromResult(ReadIdempotentResult(request.IdempotencyKey, requestHash));

            return Task.FromResult(new ProvisioningEnqueueResult(
                operation.Id,
                operation.State,
                Replayed: false,
                LockConflict: lockConflict,
                operation.FailureCode));
        }
    }

    public Task<ProvisioningOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(ReadOperation(operationId));
    }

    public Task<ProvisioningRequest?> GetRequestAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT request_json FROM operations WHERE id = $id;";
            command.Parameters.AddWithValue("$id", operationId.ToString("D"));
            var json = command.ExecuteScalar() as string;
            return Task.FromResult(json is null ? null : DeserializeRequest(json));
        }
    }

    public Task<ProvisioningOperation?> FindRunningAsync(
        ProvisioningStackKind kind,
        string stackName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, ProvisioningStackName.Canonicalize(stackName));
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id FROM operations
                WHERE lock_key = $lock AND state IN ('Running', 'Cancelling')
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$lock", lockKey);
            var id = command.ExecuteScalar() as string;
            return Task.FromResult(id is null ? null : ReadOperation(Guid.Parse(id)));
        }
    }

    public Task<IReadOnlyList<ProvisioningOperation>> ListRunnableAsync(int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id FROM operations
                WHERE state = 'Queued'
                ORDER BY accepted_at
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            var ids = new List<Guid>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                ids.Add(Guid.Parse(reader.GetString(0)));
            reader.Close();
            IReadOnlyList<ProvisioningOperation> result = ids
                .Select(ReadOperation)
                .Where(operation => operation is not null)
                .Cast<ProvisioningOperation>()
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<ProvisioningOperation?> ClaimAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var operation = ReadOperation(operationId);
            if (operation is null || operation.State != ProvisioningOperationState.Queued)
                return Task.FromResult<ProvisioningOperation?>(null);

            var lockKey = ProvisioningStackLockKey.Format(operation.StackKind, operation.StackName);
            if (operation.Command != ProvisioningCommand.Cancel && LockHeld(lockKey))
            {
                var conflicted = ProvisioningOperationStateMachine.Transition(
                    operation,
                    ProvisioningOperationState.LockConflict,
                    now,
                    ProvisioningFailureCodes.LockConflict);
                UpdateOperation(conflicted);
                return Task.FromResult<ProvisioningOperation?>(conflicted);
            }

            using var transaction = _connection.BeginTransaction();
            var running = ProvisioningOperationStateMachine.Transition(
                operation,
                ProvisioningOperationState.Running,
                now);
            UpdateOperation(running, transaction);
            if (operation.Command != ProvisioningCommand.Cancel)
                SetLock(lockKey, operation.Id, transaction);
            transaction.Commit();
            return Task.FromResult<ProvisioningOperation?>(running);
        }
    }

    public Task<ProvisioningOperation?> CompleteAsync(
        Guid operationId,
        ProvisioningOperationState state,
        DateTimeOffset now,
        string? failureCode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var operation = ReadOperation(operationId);
            if (operation is null)
                return Task.FromResult<ProvisioningOperation?>(null);

            using var transaction = _connection.BeginTransaction();
            var completed = ProvisioningOperationStateMachine.Transition(operation, state, now, failureCode);
            UpdateOperation(completed, transaction);
            var lockKey = ProvisioningStackLockKey.Format(operation.StackKind, operation.StackName);
            if (ProvisioningOperationStateMachine.IsTerminal(completed.State)
                && Holder(lockKey, transaction) == operationId)
            {
                ClearLock(lockKey, transaction);
            }

            transaction.Commit();
            return Task.FromResult<ProvisioningOperation?>(completed);
        }
    }

    public Task AppendAsync(IReadOnlyList<ProvisioningEvent> events, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(events);
        lock (_gate)
        {
            foreach (var value in events)
            {
                var sanitised = ProvisioningEventSanitizer.Sanitize(value);
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO events (operation_id, kind, resource_kind, message, occurred_at)
                    VALUES ($id, $kind, $resource, $message, $at);
                    """;
                command.Parameters.AddWithValue("$id", sanitised.OperationId.ToString("D"));
                command.Parameters.AddWithValue("$kind", sanitised.Kind.ToString());
                command.Parameters.AddWithValue("$resource", sanitised.ResourceKind);
                command.Parameters.AddWithValue("$message", sanitised.Message);
                command.Parameters.AddWithValue("$at", sanitised.OccurredAt.ToString("O"));
                command.ExecuteNonQuery();
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProvisioningEvent>> ListAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT kind, resource_kind, message, occurred_at
                FROM events
                WHERE operation_id = $id
                ORDER BY rowid;
                """;
            command.Parameters.AddWithValue("$id", operationId.ToString("D"));
            var list = new List<ProvisioningEvent>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ProvisioningEvent(
                    operationId,
                    Enum.Parse<ProvisioningEventKind>(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3))));
            }

            IReadOnlyList<ProvisioningEvent> result = list;
            return Task.FromResult(result);
        }
    }

    public Task<bool> TryAcquireAsync(
        ProvisioningStackKind kind,
        string stackName,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, stackName);
        lock (_gate)
        {
            if (LockHeld(lockKey))
                return Task.FromResult(false);
            SetLock(lockKey, operationId);
            return Task.FromResult(true);
        }
    }

    public Task ReleaseAsync(
        ProvisioningStackKind kind,
        string stackName,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, stackName);
        lock (_gate)
        {
            if (Holder(lockKey) == operationId)
                ClearLock(lockKey);
        }

        return Task.CompletedTask;
    }

    public Task<Guid?> GetHolderAsync(
        ProvisioningStackKind kind,
        string stackName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(Holder(ProvisioningStackLockKey.Format(kind, stackName)));
    }

    public async Task<IReadOnlyList<ProvisioningOperation>> FailInterruptedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<Guid> interrupted;
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id FROM operations
                WHERE state IN ('Running', 'Cancelling');
                """;
            interrupted = [];
            using var reader = command.ExecuteReader();
            while (reader.Read())
                interrupted.Add(Guid.Parse(reader.GetString(0)));
        }

        var failed = new List<ProvisioningOperation>();
        foreach (var id in interrupted)
        {
            var completed = await CompleteAsync(
                id,
                ProvisioningOperationState.Failed,
                now,
                ProvisioningFailureCodes.Interrupted,
                CancellationToken.None).ConfigureAwait(false);
            if (completed is not null)
                failed.Add(completed);
        }

        return failed;
    }

    public void Dispose() => _connection.Dispose();

    private void EnsureSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS operations (
              id TEXT PRIMARY KEY,
              command TEXT NOT NULL,
              state TEXT NOT NULL,
              provider_id TEXT NOT NULL,
              stack_kind TEXT NOT NULL,
              stack_name TEXT NOT NULL,
              lock_key TEXT NOT NULL,
              idempotency_key TEXT NOT NULL UNIQUE,
              request_hash TEXT NOT NULL,
              accepted_at TEXT NOT NULL,
              started_at TEXT,
              completed_at TEXT,
              failure_code TEXT,
              request_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS events (
              rowid INTEGER PRIMARY KEY AUTOINCREMENT,
              operation_id TEXT NOT NULL,
              kind TEXT NOT NULL,
              resource_kind TEXT NOT NULL,
              message TEXT NOT NULL,
              occurred_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS locks (
              lock_key TEXT PRIMARY KEY,
              operation_id TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private ProvisioningEnqueueResult ReadIdempotentResult(string idempotencyKey, string requestHash)
    {
        using var existing = _connection.CreateCommand();
        existing.CommandText = "SELECT id, state, request_hash, failure_code FROM operations WHERE idempotency_key = $key LIMIT 1;";
        existing.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = existing.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("Idempotent provisioning request could not be loaded.");

        var existingHash = reader.GetString(2);
        if (!string.Equals(existingHash, requestHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Idempotency key is already bound to a different provisioning request.");
        }

        var state = Enum.Parse<ProvisioningOperationState>(reader.GetString(1));
        return new ProvisioningEnqueueResult(
            Guid.Parse(reader.GetString(0)),
            state,
            Replayed: true,
            LockConflict: state == ProvisioningOperationState.LockConflict,
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private bool TryInsertOperation(ProvisioningOperation operation, ProvisioningRequest request)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operations (
              id, command, state, provider_id, stack_kind, stack_name, lock_key,
              idempotency_key, request_hash, accepted_at, started_at, completed_at,
              failure_code, request_json)
            VALUES (
              $id, $command, $state, $provider, $kind, $stack, $lock,
              $idem, $hash, $accepted, $started, $completed, $failure, $request)
            ON CONFLICT(idempotency_key) DO NOTHING;
            """;
        BindOperation(command, operation, SerializeRequest(request));
        return command.ExecuteNonQuery() == 1;
    }

    private void UpdateOperation(ProvisioningOperation operation, SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE operations
            SET state = $state, started_at = $started, completed_at = $completed, failure_code = $failure
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
        command.Parameters.AddWithValue("$state", operation.State.ToString());
        command.Parameters.AddWithValue("$started", (object?)operation.StartedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", (object?)operation.CompletedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$failure", (object?)operation.FailureCode ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private ProvisioningOperation? ReadOperation(Guid operationId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT command, state, provider_id, stack_kind, stack_name, idempotency_key,
                   request_hash, accepted_at, started_at, completed_at, failure_code
            FROM operations WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new ProvisioningOperation(
            operationId,
            Enum.Parse<ProvisioningCommand>(reader.GetString(0)),
            Enum.Parse<ProvisioningOperationState>(reader.GetString(1)),
            reader.GetString(2),
            Enum.Parse<ProvisioningStackKind>(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            startedAt: reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
            completedAt: reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            failureCode: reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private bool LockHeld(string lockKey) => Holder(lockKey) is not null;

    private bool HasLockHoldingOperation(string lockKey)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM operations
            WHERE lock_key = $lock AND state IN ('Running', 'Cancelling')
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$lock", lockKey);
        return command.ExecuteScalar() is not null;
    }

    private Guid? Holder(string lockKey, SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT operation_id FROM locks WHERE lock_key = $lock;";
        command.Parameters.AddWithValue("$lock", lockKey);
        return command.ExecuteScalar() is string id ? Guid.Parse(id) : null;
    }

    private void SetLock(string lockKey, Guid operationId, SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO locks (lock_key, operation_id) VALUES ($lock, $id)
            ON CONFLICT(lock_key) DO UPDATE SET operation_id = excluded.operation_id;
            """;
        command.Parameters.AddWithValue("$lock", lockKey);
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private void ClearLock(string lockKey, SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM locks WHERE lock_key = $lock;";
        command.Parameters.AddWithValue("$lock", lockKey);
        command.ExecuteNonQuery();
    }

    private static void BindOperation(SqliteCommand command, ProvisioningOperation operation, string requestJson)
    {
        command.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
        command.Parameters.AddWithValue("$command", operation.Command.ToString());
        command.Parameters.AddWithValue("$state", operation.State.ToString());
        command.Parameters.AddWithValue("$provider", operation.ProviderId);
        command.Parameters.AddWithValue("$kind", operation.StackKind.ToString());
        command.Parameters.AddWithValue("$stack", operation.StackName);
        command.Parameters.AddWithValue(
            "$lock",
            ProvisioningStackLockKey.Format(operation.StackKind, operation.StackName));
        command.Parameters.AddWithValue("$idem", operation.IdempotencyKey);
        command.Parameters.AddWithValue("$hash", operation.RequestHash);
        command.Parameters.AddWithValue("$accepted", operation.AcceptedAt.ToString("O"));
        command.Parameters.AddWithValue("$started", (object?)operation.StartedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", (object?)operation.CompletedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$failure", (object?)operation.FailureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$request", requestJson);
    }

    private static string SerializeRequest(ProvisioningRequest request) =>
        JsonSerializer.Serialize(RequestRecord.From(request), Json);

    private static ProvisioningRequest DeserializeRequest(string json) =>
        JsonSerializer.Deserialize<RequestRecord>(json, Json)!.ToRequest();

    private sealed record RequestRecord(
        ProvisioningCommand Command,
        string RuntimeImage,
        string[] Capabilities,
        string? DatabaseKind,
        string? SecretsKind,
        string? EndpointKind,
        string? ScaleKind,
        string ProviderId,
        ProvisioningStackKind StackKind,
        string StackName,
        string CredentialKind,
        string? CredentialLocator,
        string StateBackendKind,
        string StateBackendLocator,
        string SecretsProviderKind,
        string SecretsProviderLocator,
        string? PlatformStackName,
        string IdempotencyKey,
        Dictionary<string, string> StackConfig)
    {
        public static RequestRecord From(ProvisioningRequest request) =>
            new(
                request.Command,
                request.Spec.RuntimeImage,
                request.Spec.RequiredCapabilities.Order(StringComparer.Ordinal).ToArray(),
                request.Spec.DatabaseKind,
                request.Spec.SecretsKind,
                request.Spec.EndpointKind,
                request.Spec.ScaleKind,
                request.Target.ProviderId,
                request.Target.StackKind,
                request.Target.StackName,
                request.Target.Credentials.Kind,
                request.Target.Credentials.Locator,
                request.Target.StateBackend.Kind,
                request.Target.StateBackend.Locator,
                request.Target.SecretsProvider.Kind,
                request.Target.SecretsProvider.Locator,
                request.Target.PlatformStackName,
                request.IdempotencyKey,
                new Dictionary<string, string>(request.StackConfig, StringComparer.Ordinal));

        public ProvisioningRequest ToRequest() =>
            new(
                Command,
                new EngineSpec(
                    RuntimeImage,
                    new HashSet<string>(Capabilities, StringComparer.Ordinal),
                    DatabaseKind,
                    SecretsKind,
                    EndpointKind,
                    ScaleKind),
                new ProvisioningTarget(
                    ProviderId,
                    StackKind,
                    StackName,
                    new CredentialReference(CredentialKind, CredentialLocator),
                    new StateBackendReference(StateBackendKind, StateBackendLocator),
                    new SecretsProviderReference(SecretsProviderKind, SecretsProviderLocator),
                    PlatformStackName),
                IdempotencyKey,
                StackConfig);
    }
}
