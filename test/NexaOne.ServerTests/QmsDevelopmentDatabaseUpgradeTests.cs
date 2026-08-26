using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class QmsDevelopmentDatabaseUpgradeTests
{
    [Fact]
    public void Existing_development_database_repairs_missing_qms_sample_lot_references_on_restart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nexa-qms-dev-upgrade-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath};Foreign Keys=False";
        try
        {
            using (var initialFactory = new DevelopmentFactory(connectionString))
            using (initialFactory.CreateClient())
            {
                Count(connectionString, "IVT_MATERIAL_LOT", "LOT_IN_001").Should().Be(1);
                Count(connectionString, "POM_LOT", "LOT_PR_001").Should().Be(1);
                Count(connectionString, "POM_LOT", "LOT_SH_001").Should().Be(1);
            }

            Execute(connectionString, """
                DELETE FROM IVT_MATERIAL_LOT WHERE LOT_ID = 'LOT_IN_001';
                DELETE FROM POM_LOT WHERE LOT_ID IN ('LOT_PR_001', 'LOT_SH_001');
                """);

            using (var upgradedFactory = new DevelopmentFactory(connectionString))
            using (upgradedFactory.CreateClient())
            {
                Count(connectionString, "IVT_MATERIAL_LOT", "LOT_IN_001").Should().Be(1);
                Count(connectionString, "POM_LOT", "LOT_PR_001").Should().Be(1);
                Count(connectionString, "POM_LOT", "LOT_SH_001").Should().Be(1);
            }
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* temporary database cleanup failure is non-fatal */ }
        }
    }

    private sealed class DevelopmentFactory : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;

        public DevelopmentFactory(string connectionString) => _connectionString = connectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", _connectionString);
            builder.UseSetting("Jwt:SecretKey", "qms-development-upgrade-secret-key-at-least-32bytes");
            builder.UseSetting("Jwt:Issuer", "qms-development-upgrade-test");
            builder.UseSetting("Jwt:Audience", "qms-development-upgrade-test");
            builder.UseSetting("RateLimiting:Enabled", "false");
        }
    }

    private static long Count(string connectionString, string table, string lotId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE LOT_ID = @lotId";
        command.Parameters.AddWithValue("@lotId", lotId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
