using System.Data.Common;
using System.Text.RegularExpressions;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.ERP.Infrastructure;

/// <summary>
/// Reconciles the SQLite billing schema for invoice-linked credit notes and public delivery links.
/// SQL Server receives the equivalent contracts from V189/V190; SQLite requires a table rebuild
/// for V189 and module-owned additive tables for V190.
/// </summary>
public sealed class ErpBillingSqliteSchemaContribution : ISqliteSchemaContribution
{
    public string Id => "ERP.Billing.V190";

    public void Apply(DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        Execute(connection, transaction, "PRAGMA defer_foreign_keys = ON;");

        if (HasTable(connection, transaction, "ERP_BILLING_NUMBER"))
        {
            var numberDefinition = Definition(connection, transaction, "ERP_BILLING_NUMBER");
            if (!Regex.IsMatch(numberDefinition, @"KIND\s+IN\s*\(\s*0\s*,\s*1\s*,\s*2\s*\)", RegexOptions.IgnoreCase))
            {
                Execute(connection, transaction, """
                    CREATE TABLE ERP_BILLING_NUMBER_V189 (
                        TENANT_ID TEXT NOT NULL, ORGANIZATION_ID TEXT NOT NULL,
                        KIND INTEGER NOT NULL, NEXT_NUMBER INTEGER NOT NULL,
                        CONSTRAINT PK_ERP_BILLING_NUMBER PRIMARY KEY (TENANT_ID, ORGANIZATION_ID, KIND),
                        CONSTRAINT CK_ERP_BILLING_NUMBER_KIND CHECK (KIND IN (0,1,2)),
                        CONSTRAINT CK_ERP_BILLING_NUMBER_NEXT CHECK (NEXT_NUMBER > 0));
                    INSERT INTO ERP_BILLING_NUMBER_V189 (TENANT_ID,ORGANIZATION_ID,KIND,NEXT_NUMBER)
                        SELECT TENANT_ID,ORGANIZATION_ID,KIND,NEXT_NUMBER FROM ERP_BILLING_NUMBER;
                    DROP TABLE ERP_BILLING_NUMBER;
                    ALTER TABLE ERP_BILLING_NUMBER_V189 RENAME TO ERP_BILLING_NUMBER;
                    """);
            }
        }

        const string table = "ERP_BILLING_DOCUMENT";
        if (!HasTable(connection, transaction, table)) return;
        if (!HasColumn(connection, transaction, table, "CREDITED"))
            Execute(connection, transaction,
                "ALTER TABLE ERP_BILLING_DOCUMENT ADD COLUMN CREDITED TEXT NOT NULL DEFAULT '0';");
        if (!HasColumn(connection, transaction, table, "ADJUSTED_INVOICE_ID"))
            Execute(connection, transaction,
                "ALTER TABLE ERP_BILLING_DOCUMENT ADD COLUMN ADJUSTED_INVOICE_ID TEXT NULL;");

        var definition = Definition(connection, transaction, table);
        var hasCreditKind = Regex.IsMatch(definition,
            @"KIND\s+IN\s*\(\s*0\s*,\s*1\s*,\s*2\s*\)", RegexOptions.IgnoreCase);
        var hasCreditStatus = Regex.IsMatch(definition,
            @"STATUS\s+BETWEEN\s+0\s+AND\s+9", RegexOptions.IgnoreCase);
        var hasCreditLink = definition.Contains(
            "ADJUSTED_INVOICE_ID IS NOT NULL", StringComparison.OrdinalIgnoreCase);
        if (!hasCreditKind || !hasCreditStatus || !hasCreditLink)
        {
            var creatorForeignKey = Regex.Match(definition,
                @"CONSTRAINT\s+FK_ERP_BILLING_DOCUMENT_USER\s+FOREIGN\s+KEY\s*\(\s*CREATED_BY\s*\)\s+REFERENCES\s+(?:\[[^\]]+\]|""[^""]+""|\w+)\s*\(\s*(?:\[[^\]]+\]|""[^""]+""|\w+)\s*\)",
                RegexOptions.IgnoreCase).Value;
            var creatorForeignKeyClause = string.IsNullOrWhiteSpace(creatorForeignKey)
                ? string.Empty
                : creatorForeignKey + ",";

            var invalid = Scalar(connection, transaction, """
                SELECT COUNT(*) FROM ERP_BILLING_DOCUMENT
                 WHERE KIND NOT IN (0,1,2) OR STATUS NOT BETWEEN 0 AND 9
                    OR CREDITED IS NULL OR CREDITED LIKE '-%'
                    OR (KIND=2 AND (ADJUSTED_INVOICE_ID IS NULL OR ADJUSTED_INVOICE_ID=DOCUMENT_ID))
                    OR (KIND<>2 AND ADJUSTED_INVOICE_ID IS NOT NULL);
                """);
            if (invalid != 0)
                throw new InvalidOperationException(
                    "Legacy SQLite billing rows cannot be upgraded to the credit-note contract.");

            Execute(connection, transaction, $"""
                CREATE TABLE ERP_BILLING_DOCUMENT_V189 (
                    TENANT_ID TEXT NOT NULL, ORGANIZATION_ID TEXT NOT NULL, DOCUMENT_ID TEXT NOT NULL,
                    VERSION TEXT NOT NULL, OPERATION_ID TEXT NOT NULL, KIND INTEGER NOT NULL,
                    NUMBER INTEGER NOT NULL, STATUS INTEGER NOT NULL, CONTACT_ID TEXT NOT NULL,
                    DOCUMENT_DATE TEXT NOT NULL, DUE_DATE TEXT NOT NULL, CURRENCY TEXT NOT NULL,
                    DISCOUNT_TYPE INTEGER NULL, DISCOUNT_VALUE TEXT NULL, TAX_TYPE INTEGER NULL,
                    TAX_VALUE TEXT NULL, TAX2_TYPE INTEGER NULL, TAX2_VALUE TEXT NULL,
                    TERMS TEXT NULL, NOTE TEXT NULL, SUBTOTAL TEXT NOT NULL,
                    DISCOUNT_AMOUNT TEXT NOT NULL, TAX_AMOUNT TEXT NOT NULL, TOTAL TEXT NOT NULL,
                    PAID TEXT NOT NULL, CREDITED TEXT NOT NULL DEFAULT '0', CREATED_BY TEXT NOT NULL,
                    CONVERTED_FROM_ID TEXT NULL, CONVERTED_TO_ID TEXT NULL,
                    ADJUSTED_INVOICE_ID TEXT NULL, AT_TICKS INTEGER NOT NULL,
                    CONSTRAINT PK_ERP_BILLING_DOCUMENT PRIMARY KEY (TENANT_ID, ORGANIZATION_ID, DOCUMENT_ID),
                    CONSTRAINT UQ_ERP_BILLING_DOCUMENT_OPERATION UNIQUE (TENANT_ID, ORGANIZATION_ID, OPERATION_ID),
                    CONSTRAINT UQ_ERP_BILLING_DOCUMENT_NUMBER UNIQUE (TENANT_ID, ORGANIZATION_ID, KIND, NUMBER),
                    CONSTRAINT FK_ERP_BILLING_DOCUMENT_CONTACT FOREIGN KEY (TENANT_ID, ORGANIZATION_ID, CONTACT_ID)
                        REFERENCES ERP_BILLING_CONTACT (TENANT_ID, ORGANIZATION_ID, CONTACT_ID),
                    CONSTRAINT FK_ERP_BILLING_DOCUMENT_FROM FOREIGN KEY (TENANT_ID, ORGANIZATION_ID, CONVERTED_FROM_ID)
                        REFERENCES ERP_BILLING_DOCUMENT_V189 (TENANT_ID, ORGANIZATION_ID, DOCUMENT_ID),
                    CONSTRAINT FK_ERP_BILLING_DOCUMENT_TO FOREIGN KEY (TENANT_ID, ORGANIZATION_ID, CONVERTED_TO_ID)
                        REFERENCES ERP_BILLING_DOCUMENT_V189 (TENANT_ID, ORGANIZATION_ID, DOCUMENT_ID),
                    CONSTRAINT FK_ERP_BILLING_DOCUMENT_ADJUSTED FOREIGN KEY (TENANT_ID, ORGANIZATION_ID, ADJUSTED_INVOICE_ID)
                        REFERENCES ERP_BILLING_DOCUMENT_V189 (TENANT_ID, ORGANIZATION_ID, DOCUMENT_ID),
                    {creatorForeignKeyClause}
                    CONSTRAINT CK_ERP_BILLING_DOCUMENT_KIND CHECK (KIND IN (0,1,2)),
                    CONSTRAINT CK_ERP_BILLING_DOCUMENT_STATUS CHECK (STATUS BETWEEN 0 AND 9),
                    CONSTRAINT CK_ERP_BILLING_DOCUMENT_NUMBER CHECK (NUMBER > 0),
                    CONSTRAINT CK_ERP_BILLING_DOCUMENT_LINKS CHECK (
                        (CONVERTED_FROM_ID IS NULL OR (KIND=1 AND CONVERTED_FROM_ID<>DOCUMENT_ID))
                        AND (CONVERTED_TO_ID IS NULL OR (KIND=0 AND STATUS=2 AND CONVERTED_TO_ID<>DOCUMENT_ID))
                        AND ((KIND=2 AND ADJUSTED_INVOICE_ID IS NOT NULL AND ADJUSTED_INVOICE_ID<>DOCUMENT_ID)
                             OR (KIND<>2 AND ADJUSTED_INVOICE_ID IS NULL))),
                    CONSTRAINT CK_ERP_BILLING_DOCUMENT_AMOUNTS CHECK (
                        SUBTOTAL NOT LIKE '-%' AND DISCOUNT_AMOUNT NOT LIKE '-%' AND TAX_AMOUNT NOT LIKE '-%'
                        AND TOTAL NOT LIKE '-%' AND PAID NOT LIKE '-%' AND CREDITED NOT LIKE '-%'
                        AND LENGTH(SUBTOTAL)>0 AND LENGTH(DISCOUNT_AMOUNT)>0 AND LENGTH(TAX_AMOUNT)>0
                        AND LENGTH(TOTAL)>0 AND LENGTH(PAID)>0 AND LENGTH(CREDITED)>0)
                );
                INSERT INTO ERP_BILLING_DOCUMENT_V189
                    (TENANT_ID,ORGANIZATION_ID,DOCUMENT_ID,VERSION,OPERATION_ID,KIND,NUMBER,STATUS,CONTACT_ID,
                     DOCUMENT_DATE,DUE_DATE,CURRENCY,DISCOUNT_TYPE,DISCOUNT_VALUE,TAX_TYPE,TAX_VALUE,TAX2_TYPE,TAX2_VALUE,
                     TERMS,NOTE,SUBTOTAL,DISCOUNT_AMOUNT,TAX_AMOUNT,TOTAL,PAID,CREDITED,CREATED_BY,
                     CONVERTED_FROM_ID,CONVERTED_TO_ID,ADJUSTED_INVOICE_ID,AT_TICKS)
                SELECT TENANT_ID,ORGANIZATION_ID,DOCUMENT_ID,VERSION,OPERATION_ID,KIND,NUMBER,STATUS,CONTACT_ID,
                       DOCUMENT_DATE,DUE_DATE,CURRENCY,DISCOUNT_TYPE,DISCOUNT_VALUE,TAX_TYPE,TAX_VALUE,TAX2_TYPE,TAX2_VALUE,
                       TERMS,NOTE,SUBTOTAL,DISCOUNT_AMOUNT,TAX_AMOUNT,TOTAL,PAID,CREDITED,CREATED_BY,
                       CONVERTED_FROM_ID,CONVERTED_TO_ID,ADJUSTED_INVOICE_ID,AT_TICKS
                  FROM ERP_BILLING_DOCUMENT;
                DROP TABLE ERP_BILLING_DOCUMENT;
                ALTER TABLE ERP_BILLING_DOCUMENT_V189 RENAME TO ERP_BILLING_DOCUMENT;
                """);
        }

        Execute(connection, transaction, "CREATE UNIQUE INDEX IF NOT EXISTS UQ_ERP_BILLING_DOCUMENT_FROM ON ERP_BILLING_DOCUMENT (TENANT_ID,ORGANIZATION_ID,CONVERTED_FROM_ID) WHERE CONVERTED_FROM_ID IS NOT NULL;");
        Execute(connection, transaction, "CREATE UNIQUE INDEX IF NOT EXISTS UQ_ERP_BILLING_DOCUMENT_TO ON ERP_BILLING_DOCUMENT (TENANT_ID,ORGANIZATION_ID,CONVERTED_TO_ID) WHERE CONVERTED_TO_ID IS NOT NULL;");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_ERP_BILLING_DOCUMENT_PAGE ON ERP_BILLING_DOCUMENT (TENANT_ID,ORGANIZATION_ID,KIND,NUMBER);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_ERP_BILLING_DOCUMENT_REPORT ON ERP_BILLING_DOCUMENT (TENANT_ID,ORGANIZATION_ID,KIND,STATUS,DOCUMENT_DATE,DOCUMENT_ID);");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_ERP_BILLING_DOCUMENT_ADJUSTED ON ERP_BILLING_DOCUMENT (TENANT_ID,ORGANIZATION_ID,ADJUSTED_INVOICE_ID,NUMBER) WHERE ADJUSTED_INVOICE_ID IS NOT NULL;");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS ERP_BILLING_SHARE_LINK (
                TENANT_ID TEXT NOT NULL, ORGANIZATION_ID TEXT NOT NULL, SHARE_ID TEXT NOT NULL,
                VERSION TEXT NOT NULL, OPERATION_ID TEXT NOT NULL, DOCUMENT_ID TEXT NOT NULL,
                DOCUMENT_VERSION TEXT NOT NULL, TOKEN_HASH TEXT NOT NULL, EXPIRES_AT_TICKS INTEGER NOT NULL,
                CREATED_BY TEXT NOT NULL, CREATED_AT_TICKS INTEGER NOT NULL, REVOKED_AT_TICKS INTEGER NULL,
                ACCESS_COUNT INTEGER NOT NULL DEFAULT 0, LAST_ACCESSED_AT_TICKS INTEGER NULL, PAYLOAD TEXT NOT NULL,
                CONSTRAINT PK_ERP_BILLING_SHARE_LINK PRIMARY KEY (TENANT_ID,ORGANIZATION_ID,SHARE_ID),
                CONSTRAINT UQ_ERP_BILLING_SHARE_OPERATION UNIQUE (TENANT_ID,ORGANIZATION_ID,OPERATION_ID),
                CONSTRAINT UQ_ERP_BILLING_SHARE_TOKEN UNIQUE (TOKEN_HASH),
                CONSTRAINT FK_ERP_BILLING_SHARE_DOCUMENT FOREIGN KEY (TENANT_ID,ORGANIZATION_ID,DOCUMENT_ID)
                    REFERENCES ERP_BILLING_DOCUMENT (TENANT_ID,ORGANIZATION_ID,DOCUMENT_ID),
                CONSTRAINT CK_ERP_BILLING_SHARE_EXPIRY CHECK (EXPIRES_AT_TICKS>CREATED_AT_TICKS),
                CONSTRAINT CK_ERP_BILLING_SHARE_ACCESS CHECK (ACCESS_COUNT>=0));
            CREATE INDEX IF NOT EXISTS IX_ERP_BILLING_SHARE_DOCUMENT ON ERP_BILLING_SHARE_LINK
                (TENANT_ID,ORGANIZATION_ID,DOCUMENT_ID,CREATED_AT_TICKS,SHARE_ID);
            CREATE TABLE IF NOT EXISTS ERP_BILLING_SHARE_ACCESS (
                TENANT_ID TEXT NOT NULL, ORGANIZATION_ID TEXT NOT NULL, ACCESS_ID TEXT NOT NULL,
                SHARE_ID TEXT NOT NULL, ACCESSED_AT_TICKS INTEGER NOT NULL,
                CLIENT_ADDRESS_HASH TEXT NULL, USER_AGENT TEXT NULL,
                CONSTRAINT PK_ERP_BILLING_SHARE_ACCESS PRIMARY KEY (TENANT_ID,ORGANIZATION_ID,ACCESS_ID),
                CONSTRAINT FK_ERP_BILLING_SHARE_ACCESS_LINK FOREIGN KEY (TENANT_ID,ORGANIZATION_ID,SHARE_ID)
                    REFERENCES ERP_BILLING_SHARE_LINK (TENANT_ID,ORGANIZATION_ID,SHARE_ID));
            CREATE INDEX IF NOT EXISTS IX_ERP_BILLING_SHARE_ACCESS_LINK ON ERP_BILLING_SHARE_ACCESS
                (TENANT_ID,ORGANIZATION_ID,SHARE_ID,ACCESSED_AT_TICKS,ACCESS_ID);
            CREATE TABLE IF NOT EXISTS ERP_BILLING_SHARE_DELIVERY (
                TENANT_ID TEXT NOT NULL, ORGANIZATION_ID TEXT NOT NULL, SHARE_ID TEXT NOT NULL,
                DELIVERY_ID TEXT NOT NULL, CREATED_AT_TICKS INTEGER NOT NULL,
                CONSTRAINT PK_ERP_BILLING_SHARE_DELIVERY PRIMARY KEY (TENANT_ID,ORGANIZATION_ID,DELIVERY_ID),
                CONSTRAINT FK_ERP_BILLING_SHARE_DELIVERY_LINK FOREIGN KEY (TENANT_ID,ORGANIZATION_ID,SHARE_ID)
                    REFERENCES ERP_BILLING_SHARE_LINK (TENANT_ID,ORGANIZATION_ID,SHARE_ID),
                CONSTRAINT FK_ERP_BILLING_SHARE_DELIVERY_REQUEST FOREIGN KEY (TENANT_ID,ORGANIZATION_ID,DELIVERY_ID)
                    REFERENCES COL_DELIVERY_REQUEST (TENANT_ID,ORGANIZATION_ID,DELIVERY_ID));
            """);
    }

    private static string Definition(
        DbConnection connection,
        DbTransaction transaction,
        string table)
    {
        using var command = Command(connection, transaction,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name=@name;");
        Add(command, "@name", table);
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static bool HasTable(
        DbConnection connection,
        DbTransaction transaction,
        string table)
    {
        using var command = Command(connection, transaction,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name COLLATE NOCASE;");
        Add(command, "@name", table);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static bool HasColumn(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string column)
    {
        using var command = Command(connection, transaction,
            $"PRAGMA table_info([{table.Replace("]", "]]", StringComparison.Ordinal)}]);");
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static long Scalar(
        DbConnection connection,
        DbTransaction transaction,
        string commandText)
    {
        using var command = Command(connection, transaction, commandText);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(
        DbConnection connection,
        DbTransaction transaction,
        string commandText)
    {
        using var command = Command(connection, transaction, commandText);
        command.ExecuteNonQuery();
    }

    private static DbCommand Command(
        DbConnection connection,
        DbTransaction transaction,
        string commandText)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return command;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
