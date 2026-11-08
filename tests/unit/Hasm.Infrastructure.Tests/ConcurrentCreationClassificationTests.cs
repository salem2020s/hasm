using Hasm.Infrastructure.SystemDatabase;
using Npgsql;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Deterministic regression tests for how the initializer classifies CREATE DATABASE failures.
/// Only "database already exists" outcomes may be treated as a concurrent creation.
/// </summary>
public class ConcurrentCreationClassificationTests
{
    private static PostgresException Error(string sqlState, string? constraint = null) =>
        new("error", "ERROR", "ERROR", sqlState, constraintName: constraint);

    [Fact]
    public void Duplicate_database_is_a_concurrent_creation() =>
        Assert.True(SystemDatabaseInitializer.IsConcurrentCreation(Error(PostgresErrorCodes.DuplicateDatabase)));

    [Fact]
    public void Unique_violation_on_the_database_name_index_is_a_concurrent_creation() =>
        Assert.True(SystemDatabaseInitializer.IsConcurrentCreation(
            Error(PostgresErrorCodes.UniqueViolation, "pg_database_datname_index")));

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation, "pg_database_oid_index")]
    [InlineData(PostgresErrorCodes.UniqueViolation, "some_other_constraint")]
    [InlineData(PostgresErrorCodes.UniqueViolation, null)]
    [InlineData(PostgresErrorCodes.InsufficientPrivilege, "pg_database_datname_index")]
    [InlineData(PostgresErrorCodes.ObjectInUse, null)]
    [InlineData(PostgresErrorCodes.DiskFull, null)]
    public void Everything_else_is_not_swallowed(string sqlState, string? constraint) =>
        Assert.False(SystemDatabaseInitializer.IsConcurrentCreation(Error(sqlState, constraint)));
}
