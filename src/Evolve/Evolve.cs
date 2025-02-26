﻿using ConsoleTables;
using EvolveDb.Configuration;
using EvolveDb.Connection;
using EvolveDb.Dialect;
using EvolveDb.Metadata;
using EvolveDb.Migration;
using EvolveDb.Utilities;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Transactions;

[assembly: InternalsVisibleTo("Evolve.Tests")]
namespace EvolveDb
{
    public class Evolve : IEvolveConfiguration
    {
        #region Fields

        private readonly DbConnection _userCnn;
        private readonly Action<string> _log;

        #endregion

        /// <summary>
        ///     Initialize a new instance of the <see cref="Evolve"/> class.
        /// </summary>
        /// <param name="dbConnection"> The database connection used to apply the migrations. </param>
        /// <param name="logDelegate"> An optional logger. </param>
        /// <param name="dbms"> Optional default dbms</param>
        public Evolve(DbConnection dbConnection, Action<string>? logDelegate = null, DBMS? dbms = null)
        {
            _userCnn = Check.NotNull(dbConnection, nameof(dbConnection));
            _log = logDelegate ?? new Action<string>((msg) => { });

            using var evolveCnn = new WrappedConnection(_userCnn).Validate();
            DBMS = dbms ?? evolveCnn.GetDatabaseServerType();
        }

        #region IEvolveConfiguration

        public IEnumerable<string> Schemas { get; set; } = new List<string>();
        public CommandOptions Command { get; set; } = CommandOptions.DoNothing;
        public bool IsEraseDisabled { get; set; }
        public bool MustEraseOnValidationError { get; set; }
        public Encoding Encoding { get; set; } = Encoding.UTF8;
        public IEnumerable<string> Locations { get; set; } = new List<string> { "Sql_Scripts" };
        public string MetadataTableName { get; set; } = "changelog";

        private string? _metadaTableSchema;
        public string MetadataTableSchema
        {
            get => _metadaTableSchema.IsNullOrWhiteSpace() ? Schemas.First() : _metadaTableSchema!;
            set => _metadaTableSchema = value;
        }
        public string PlaceholderPrefix { get; set; } = "${";
        public string PlaceholderSuffix { get; set; } = "}";
        public Dictionary<string, string> Placeholders { get; set; } = new Dictionary<string, string>();
        public string SqlMigrationPrefix { get; set; } = "V";
        public string SqlRepeatableMigrationPrefix { get; set; } = "R";
        public string SqlMigrationSeparator { get; set; } = "__";
        public string SqlMigrationSuffix { get; set; } = ".sql";
        public MigrationVersion TargetVersion { get; set; } = MigrationVersion.MaxVersion;
        public MigrationVersion StartVersion { get; set; } = MigrationVersion.MinVersion;
        public bool EnableClusterMode { get; set; } = true;
        public bool OutOfOrder { get; set; } = false;
        public int? CommandTimeout { get; set; }
        public int? AmbientTransactionTimeout { get; set; }
        public IEnumerable<Assembly> EmbeddedResourceAssemblies { get; set; } = new List<Assembly>();
        public IEnumerable<string> EmbeddedResourceFilters { get; set; } = new List<string>();
        public bool RetryRepeatableMigrationsUntilNoError { get; set; }
        public TransactionKind TransactionMode { get; set; } = TransactionKind.CommitEach;
        public bool SkipNextMigrations { get; set; } = false;

        private IMigrationLoader? _migrationLoader;
        public IMigrationLoader MigrationLoader
        {
            get
            {
                return _migrationLoader ?? (EmbeddedResourceAssemblies.Any()
                    ? new EmbeddedResourceMigrationLoader(Options)
                    : new FileMigrationLoader(Options));
            }
            set { _migrationLoader = value; }
        }

        #endregion

        #region Properties

        /// <summary>
        ///     Returns Evolve configfuration.
        /// </summary>
        public IEvolveConfiguration Options => this;

        /// <summary>
        ///     Number of migration script applied during the last Evolve execution.
        /// </summary>
        public int NbMigration { get; private set; }

        /// <summary>
        ///     Number of migration script repaired during the last Evolve execution.
        /// </summary>
        public int NbReparation { get; private set; }

        /// <summary>
        ///     Number of database schema erased during the last Evolve execution.
        /// </summary>
        public int NbSchemaErased { get; private set; }

        /// <summary>
        ///     Number of database schema not erased during the last Evolve execution.
        /// </summary>
        public int NbSchemaToEraseSkipped { get; private set; }

        /// <summary>
        ///     Total elapsed time in milliseconds taken by the last Evolve execution.
        /// </summary>
        public long TotalTimeElapsedInMs { get; private set; }

        /// <summary>
        ///     List of the applied migrations of the last Evolve execution.
        /// </summary>
        public List<string> AppliedMigrations { get; private set; } = new();

        /// <summary>
        ///     Name of the database management system Evolve is connected to.
        /// </summary>
        public DBMS DBMS { get; }

        #endregion

        #region Commands

        public void ExecuteCommand()
        {
            switch (Command)
            {
                case CommandOptions.Migrate:
                    Migrate();
                    _log($"Evolve command 'migrate' successfully executed.");
                    break;
                case CommandOptions.Repair:
                    Repair();
                    _log($"Evolve command 'repair' successfully executed.");
                    break;
                case CommandOptions.Erase:
                    Erase();
                    _log($"Evolve command 'erase' successfully executed.");
                    break;
                case CommandOptions.Info:
                    Info();
                    break;
                case CommandOptions.Validate:
                    Validate();
                    _log($"Evolve command 'validate' successfully executed.");
                    break;
                default:
                    _log($"Evolve.Command parameter is not set. No migration applied. See: https://evolve-db.netlify.com/configuration/ for more information.");
                    break;
            }
        }

        /// <summary>
        ///     Validate Evolve configuration to detect if schema(s) could be recreated exactly.
        /// </summary>
        public void Validate()
        {
            Command = CommandOptions.Validate;

            var errors = new List<string>();
            using var db = InitiateDatabaseConnection();
            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadataTableName);
            bool isEvolveInitialized = metadata.IsEvolveInitialized();
            var lastAppliedVersion = isEvolveInitialized ? metadata.FindLastAppliedVersion() : MigrationVersion.MinVersion;
            var startVersion = isEvolveInitialized ? metadata.FindStartVersion() : MigrationVersion.MinVersion;
            if (startVersion == MigrationVersion.MinVersion)
            {
                startVersion = StartVersion;
            }

            // Each script of the applied migrations must be found and the checksum must be identical.
            var scripts = MigrationLoader.GetMigrations().Union(MigrationLoader.GetRepeatableMigrations());
            foreach (var migration in GetAllExecutedMigration(metadata))
            {
                var script = scripts.SingleOrDefault(x => x.Name == migration.Name);
                if (script is null)
                { // Missing script
                    errors.Add($"\t- missing migration: {migration.Name}");
                }
                else if (migration.Type != MetadataType.RepeatableMigration
                      && migration.Checksum is not null
                      && migration.Checksum != script.CalculateChecksum())
                { // Invalid checksum
                    errors.Add($"\t- invalid checksum for: {migration.Name}");
                }
            }

            // No pending migration must be found
            foreach (var migration in GetAllPendingMigration(startVersion, lastAppliedVersion))
            {
                errors.Add($"\t- pending migration: {migration.Name}");
            }

            // No pending repeatable migration must be found (excluding RepeatAlways migrations)
            foreach (var migration in GetAllPendingRepeatableMigration(metadata, excludeRepeatAlways: true))
            {
                errors.Add($"\t- pending repeatable migration: {migration.Name}");
            }

            if (errors.Any())
            {
                var uniqueErrors = errors.Distinct().ToList();
                throw new EvolveValidationException(
                    $"Evolve validation failed. {uniqueErrors.Count} error(s) found:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, uniqueErrors));
            }

            _log($"No validation errors found.");
        }

        /// <summary>
        ///     Returns details about migrations, what has been applied and what is pending.
        /// </summary>
        public IEnumerable<MigrationMetadataUI> Info()
        {
            Command = CommandOptions.Info;

            var table = new ConsoleTable("Id", "Version", "Category", "Description", "Installed on", "Installed by", "Success", "Checksum").Configure(o => o.EnableCount = false);
            using var db = InitiateDatabaseConnection();
            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadataTableName);
            bool isEvolveInitialized = metadata.IsEvolveInitialized();
            var lastAppliedVersion = isEvolveInitialized ? metadata.FindLastAppliedVersion() : MigrationVersion.MinVersion;
            var startVersion = isEvolveInitialized ? metadata.FindStartVersion() : MigrationVersion.MinVersion;
            if (startVersion == MigrationVersion.MinVersion)
            {
                startVersion = StartVersion;
            }
                
            var rows = new List<MigrationMetadataUI>();
            rows.AddRange(GetAllPendingSchemaUI(db, metadata));
            if (isEvolveInitialized)
            {
                rows.AddRange(GetAllBeforeFirstMigrationUI(metadata));
                rows.AddRange(GetAllIgnoredMigrationUI());
                rows.AddRange(GetAllExecutedMigrationUI(metadata));
                rows.AddRange(GetAllOutOfOrderPendingMigrationUI(metadata, startVersion, lastAppliedVersion));
                rows.AddRange(GetAllOutOfOrderPendingMigrationInfoPurposesUI(metadata, startVersion, lastAppliedVersion));
            }
            rows.AddRange(GetAllPendingMigrationUI(startVersion, lastAppliedVersion));
            rows.AddRange(GetAllPendingRepeatableMigrationUI(metadata));
            rows.AddRange(GetAllOffTargetMigrationUI());

            rows.ForEach(x => table.AddRow(x.Id, x.Version, x.Category, x.Description, x.InstalledOn, x.InstalledBy, x.Success, x.Checksum));
            _log(table.ToStringAlternative());
            return rows;

            IEnumerable<MigrationMetadataUI> GetAllPendingSchemaUI(DatabaseHelper db, IEvolveMetadata metadata)
            {
                var pendingSchemas = new List<MigrationMetadataUI>();
                foreach (var schemaName in FindSchemas())
                {
                    var schema = db.GetSchema(schemaName);
                    if (!schema.IsExists())
                    {
                        pendingSchemas.Add(new MigrationMetadataUI("0", $"Create new schema: {schemaName}.", "Pending"));
                    }
                    else if (schema.IsEmpty() && !metadata.IsEmptySchemaMetadataExists(schemaName))
                    {
                        pendingSchemas.Add(new MigrationMetadataUI("0", $"Empty schema found: {schemaName}.", "Pending"));
                    }
                }

                return pendingSchemas;
            }

            static IEnumerable<MigrationMetadataUI> GetAllBeforeFirstMigrationUI(IEvolveMetadata metadata)
            {
                return metadata.GetAllMetadata()
                               .Where(x => x.Version != null)
                               .OrderBy(x => x.Version)
                               .ThenBy(x => x.InstalledOn)
                               .TakeWhile(x => x.Type != MetadataType.Migration)
                               .Select(x => new MigrationMetadataUI(x));
            }

            IEnumerable<MigrationMetadataUI> GetAllIgnoredMigrationUI()
            {
                return MigrationLoader.GetMigrations()
                                      .TakeWhile(x => x.Version < StartVersion)
                                      .Select(x => new MigrationMetadataUI(x.Version?.Label, x.Description, "Ignored"));
            }

            IEnumerable<MigrationMetadataUI> GetAllExecutedMigrationUI(IEvolveMetadata metadata)
            {
                return GetAllExecutedMigration(metadata).Select(x => new MigrationMetadataUI(x));
            }

            IEnumerable<MigrationMetadataUI> GetAllOutOfOrderPendingMigrationUI(IEvolveMetadata metadata, MigrationVersion startVersion, MigrationVersion lastAppliedVersion)
            {
                return GetAllOutOfOrderPendingMigration(metadata, startVersion, lastAppliedVersion)
                    .Select(x => new MigrationMetadataUI(x.Version?.Label, x.Description, "Pending"));
            }

            IEnumerable<MigrationMetadataUI> GetAllOutOfOrderPendingMigrationInfoPurposesUI(IEvolveMetadata metadata, MigrationVersion startVersion, MigrationVersion lastAppliedVersion)
            {
                return GetAllOutOfOrderPendingMigrationNoMigration(metadata, startVersion, lastAppliedVersion)
                    .Select(x => new MigrationMetadataUI(x.Version?.Label, x.Description, "Lost"));
            }
            
            IEnumerable<MigrationMetadataUI> GetAllPendingMigrationUI(MigrationVersion startVersion, MigrationVersion lastAppliedVersion)
            {
                return GetAllPendingMigration(startVersion, lastAppliedVersion)
                    .Select(x => new MigrationMetadataUI(x.Version?.Label, x.Description, "Pending"));
            }

            IEnumerable<MigrationMetadataUI> GetAllPendingRepeatableMigrationUI(IEvolveMetadata metadata)
            {
                return GetAllPendingRepeatableMigration(metadata)
                    .Select(x => new MigrationMetadataUI(x.Version?.Label, x.Description, "Pending"));
            }

            IEnumerable<MigrationMetadataUI> GetAllOffTargetMigrationUI()
            {
                return MigrationLoader.GetMigrations()
                                      .SkipWhile(x => x.Version < TargetVersion)
                                      .Select(x => new MigrationMetadataUI(x.Version?.Label, x.Description, "Ignored"));
            }
        }

        private IEnumerable<MigrationMetadata> GetAllExecutedMigration(IEvolveMetadata metadata)
        {
            if (!metadata.IsEvolveInitialized())
            {
                return Enumerable.Empty<MigrationMetadata>();
            }

            if (DBMS == DBMS.Cassandra)
            { // Cassandra has not a monotonic Id. We have to customize the order.
                var executedMigrations = metadata.GetAllAppliedMigration().ToList();
                executedMigrations.AddRange(metadata.GetAllAppliedRepeatableMigration()
                                                    .OrderBy(x => x.InstalledOn)
                                                    .ThenBy(x => x.Name));

                return executedMigrations;
            }

            return metadata.GetAllMetadata()
                           .Where(x => x.Type == MetadataType.Migration || x.Type == MetadataType.RepeatableMigration)
                           .OrderBy(x => x.Id);
        }

        /// <summary>
        ///     Migrates the database.
        /// </summary>
        public void Migrate()
        {
            Command = CommandOptions.Migrate;
            _log("Executing Migrate...");

            InternalExecuteCommand(db =>
            {
                InternalMigrate(db);
            });
        }

        private void InternalMigrate(DatabaseHelper db)
        {
            try
            {
                ValidateAndRepairMetadata(db);
            }
            catch (EvolveValidationException ex)
            {
                if (MustEraseOnValidationError)
                {
                    _log($"{ex.Message} Erase database. (MustEraseOnValidationError = True)");

                    InternalErase(db);
                    ManageSchemas(db);
                }
                else
                {
                    throw;
                }
            }

            if (MigrationLoader.GetMigrations().Count() == 0 && MigrationLoader.GetRepeatableMigrations().Count() == 0)
            {
                _log("No migration script found.");
                return;
            }

            MigrationVersion lastAppliedVersion;
            if (TransactionMode == TransactionKind.CommitEach)
            {
                lastAppliedVersion = Migrate();
            }
            else
            {
                TransactionScope scope;
                var defaultAmbientTransactionTimeout = TransactionManager.DefaultTimeout;
                if (AmbientTransactionTimeout != null)
                {
                    var newAmbientTransactionTimeout = new TimeSpan(0, 0, AmbientTransactionTimeout.Value);
                    ConfigureTransactionTimeoutCore(newAmbientTransactionTimeout);
                    scope = new TransactionScope(TransactionScopeOption.Required, newAmbientTransactionTimeout);
                }
                else
                {
                    scope = new TransactionScope();
                }

                try
                {
                    db.WrappedConnection.UseAmbientTransaction();
                    lastAppliedVersion = Migrate();

                    if (TransactionMode == TransactionKind.CommitAll)
                    {
                        scope.Complete();
                    }
                    else
                    {
                        LogRollbackAppliedMigration();
                    }
                }
                finally
                {
                    scope.Dispose();
                    if (AmbientTransactionTimeout != null)
                    {
                        ConfigureTransactionTimeoutCore(defaultAmbientTransactionTimeout);
                    }
                }
            }

            if (NbMigration == 0)
            {
                _log("Database is up to date. No migration needed.");
            }
            else
            {
                if (TransactionMode == TransactionKind.RollbackAll)
                {
                    _log($"Database migration tested to version {lastAppliedVersion}. 0 migration applied. {NbMigration} migration(s) tested in {TotalTimeElapsedInMs} ms.");
                }
                else
                {
                    _log($"Database migrated to version {lastAppliedVersion}. {NbMigration} migration(s) applied in {TotalTimeElapsedInMs} ms.");
                }
            }

            MigrationVersion Migrate()
            {
                ExecuteAllOutOfOrderMigration(db);
                var lastAppliedVersion = ExecuteAllMigration(db);
                ExecuteAllRepeatableMigration(db);
                return lastAppliedVersion;
            }
        }

        private static void ConfigureTransactionTimeoutCore(TimeSpan timeout)
        {
            SetTransactionManagerField("s_cachedMaxTimeout", true);
            SetTransactionManagerField("s_maximumTimeout", timeout);

            static void SetTransactionManagerField(string fieldName, object value)
            {
                typeof(TransactionManager)
                    .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!
                    .SetValue(null, value);
            }
        }

        /// <summary>
        ///     Execute OutOfOrder migration when allowed and needed
        /// </summary>
        private void ExecuteAllOutOfOrderMigration(DatabaseHelper db)
        {
            if (!OutOfOrder)
            {
                return;
            }

            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadataTableName);
            var startVersion = metadata.FindStartVersion();
            var lastAppliedVersion = metadata.FindLastAppliedVersion();

            foreach (var migration in GetAllOutOfOrderPendingMigration(metadata, startVersion, lastAppliedVersion))
            {
                ExecuteMigration(migration, db);
            }
        }

        /// <summary>
        ///     Execute new versioned migrations considering <see cref="StartVersion"/> and <see cref="TargetVersion"/>.
        /// </summary>
        /// <returns> The version of the last applied versioned migration or <see cref="MigrationVersion.MinVersion"/> if none. </returns>
        private MigrationVersion ExecuteAllMigration(DatabaseHelper db)
        {
            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadataTableName);
            var startVersion = metadata.FindStartVersion();
            var lastAppliedVersion = metadata.FindLastAppliedVersion();
            var migrations = GetAllPendingMigration(startVersion, lastAppliedVersion);

            foreach (var migration in migrations)
            {
                if (SkipNextMigrations)
                {
                    SkipMigration(migration, db);
                }
                else
                {
                    ExecuteMigration(migration, db);
                }
            }

            return migrations.Any() ? migrations.Last().Version! : lastAppliedVersion;
        }

        private IEnumerable<MigrationScript> GetAllPendingMigration(MigrationVersion startVersion, MigrationVersion lastAppliedVersion)
        {
            return MigrationLoader.GetMigrations()
                                  .SkipWhile(x => x.Version < startVersion)
                                  .SkipWhile(x => x.Version <= lastAppliedVersion)
                                  .TakeWhile(x => x.Version <= TargetVersion);
        }

        IEnumerable<MigrationScript> GetAllOutOfOrderPendingMigration(IEvolveMetadata metadata, MigrationVersion startVersion, MigrationVersion lastAppliedVersion)
        {
            if (!OutOfOrder)
            {
                return Enumerable.Empty<MigrationScript>();
            }

            var pendingMigrations = new List<MigrationScript>();
            var appliedMigrations = metadata.GetAllAppliedMigration();
            var scripts = MigrationLoader.GetMigrations()
                                         .SkipWhile(x => x.Version < startVersion)
                                         .TakeWhile(x => x.Version <= lastAppliedVersion);

            foreach (var script in scripts)
            {
                var appliedMigration = appliedMigrations.SingleOrDefault(x => x.Version == script.Version);
                if (appliedMigration is null)
                {
                    pendingMigrations.Add(script);
                }
            }

            return pendingMigrations;
        }

        IEnumerable<MigrationScript> GetAllOutOfOrderPendingMigrationNoMigration(IEvolveMetadata metadata, MigrationVersion startVersion, MigrationVersion lastAppliedVersion)
        {
            var pendingMigrations = new List<MigrationScript>();
            var appliedMigrations = metadata.GetAllAppliedMigration();
            var scripts = MigrationLoader.GetMigrations()
                .SkipWhile(x => x.Version < startVersion)
                .TakeWhile(x => x.Version <= lastAppliedVersion);

            foreach (var script in scripts)
            {
                var appliedMigration = appliedMigrations.SingleOrDefault(x => x.Version == script.Version);
                if (appliedMigration is null)
                {
                    pendingMigrations.Add(script);
                }
            }

            return pendingMigrations;
        }

        /// <summary>
        ///     Execute new repeatable migrations and all those for which the checksum has changed since the last execution.
        /// </summary>
        private void ExecuteAllRepeatableMigration(DatabaseHelper db)
        {
            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadataTableName);
            var pendingMigrations = GetAllPendingRepeatableMigration(metadata).ToList();
            if (pendingMigrations.Count == 0)
            {
                return;
            }

            if (!RetryRepeatableMigrationsUntilNoError)
            { // default
                foreach (var migration in pendingMigrations)
                {
                    ExecuteMigration(migration, db);
                }
            }
            else
            { // RetryRepeatableMigrationsUntilNoError
                List<MigrationScript> executedMigrations = new();
                List<Exception> exceptions;
                int executedCount;

                do
                {
                    exceptions = new();
                    executedCount = 0;

                    foreach (var migration in pendingMigrations)
                    {
                        try
                        {

                            if (!executedMigrations.Contains(migration))
                            {
                                ExecuteMigration(migration, db);

                                executedMigrations.Add(migration);
                                executedCount += 1;
                            }
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                } while (executedMigrations.Count != pendingMigrations.Count && executedCount > 0);

                if (exceptions.Any())
                {
                    throw exceptions.First();
                }
            }
        }

        private IEnumerable<MigrationScript> GetAllPendingRepeatableMigration(IEvolveMetadata metadata, bool excludeRepeatAlways = false)
        {
            if (!metadata.IsEvolveInitialized())
            {
                return MigrationLoader.GetRepeatableMigrations();
            }

            var pendingMigrations = new List<MigrationScript>();
            var appliedMigrations = metadata.GetAllAppliedRepeatableMigration();
            var scripts = MigrationLoader.GetRepeatableMigrations();
            
            foreach (var script in scripts)
            {
                var appliedMigration = appliedMigrations.Where(x => x.Name == script.Name).OrderBy(x => x.InstalledOn).LastOrDefault();
                if (appliedMigration is null
                 || (script.MustRepeatAlways && !excludeRepeatAlways)
                 || appliedMigration.Checksum != script.CalculateChecksum())
                {
                    pendingMigrations.Add(script);
                }
            }

            return pendingMigrations;
        }

        /// <summary>
        ///     Corrects checksums of the applied migrations in the metadata table, with the ones from migration scripts.
        /// </summary>
        public void Repair()
        {
            Command = CommandOptions.Repair;
            _log("Executing Repair...");

            InternalExecuteCommand(db =>
            {
                ValidateAndRepairMetadata(db);

                if (NbReparation == 0)
                {
                    _log("Metadata are up to date. Repair cancelled.");
                }
                else
                {
                    _log($"Successfully repaired {NbReparation} migration(s).");
                }
            });
        }

        /// <summary>
        ///     Erases the database schemas listed in <see cref="Schemas"/>.
        ///     Only works if Evolve has created the schema at first or found it empty.
        /// </summary>
        public void Erase()
        {
            Command = CommandOptions.Erase;

            InternalExecuteCommand(db =>
            {
                InternalErase(db);
            });
        }

        private void InternalErase(DatabaseHelper db)
        {
            _log("Executing Erase...");

            if (IsEraseDisabled)
            {
                throw new EvolveConfigurationException("Erase is disabled.");
            }

            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadataTableName);

            if (!metadata.IsExists())
            {
                _log("No metadata found. Erase cancelled.");
                return;
            }

            if (!db.WrappedConnection.CassandraCluster)
            {
                db.WrappedConnection.TryBeginTransaction();
            }

            foreach (var schemaName in FindSchemas().Reverse())
            {
                if (metadata.CanDropSchema(schemaName))
                {
                    try
                    {
                        db.GetSchema(schemaName).Drop();
                        _log($"Successfully dropped schema {schemaName}.");
                        NbSchemaErased++;
                    }
                    catch (Exception ex)
                    {
                        throw new EvolveException($"Erase failed. Impossible to drop schema {schemaName}.", ex);
                    }
                }
                else if (metadata.CanEraseSchema(schemaName))
                {
                    try
                    {
                        db.GetSchema(schemaName).Erase();
                        _log($"Successfully erased schema {schemaName}.");
                        NbSchemaErased++;
                    }
                    catch (Exception ex)
                    {
                        throw new EvolveException($"Erase failed. Impossible to erase schema {schemaName}.", ex);
                    }
                }
                else
                {
                    _log($"Cannot erase schema {schemaName}. This schema was not empty when Evolve first started migrations.");
                    NbSchemaToEraseSkipped++;
                }
            }

            if (!db.WrappedConnection.CassandraCluster)
            {
                db.WrappedConnection.TryCommit();
            }

            _log($"Erase schema(s) completed: {NbSchemaErased} erased, {NbSchemaToEraseSkipped} skipped.");
        }

        #endregion

        private void InternalExecuteCommand(Action<DatabaseHelper> commandAction)
        {
            NbMigration = 0;
            NbReparation = 0;
            NbSchemaErased = 0;
            NbSchemaToEraseSkipped = 0;
            TotalTimeElapsedInMs = 0;
            AppliedMigrations = new();

            using var db = InitiateDatabaseConnection();

            if (EnableClusterMode)
            {
                WaitForApplicationLock(db);
            }

            try
            {
                ManageSchemas(db); // Ensures all schema are created before using the metadatatable

                if (EnableClusterMode)
                {
                    WaitForMetadataTableLock(db);
                }

                ManageStartVersion(db);

                commandAction(db);
            }
            finally
            {
                if (EnableClusterMode)
                {
                    var metadata = db.GetMetadataTable(MetadataTableSchema, MetadataTableName);
                    if (!db.ReleaseApplicationLock() || !metadata.ReleaseLock())
                    {
                        _log("Error trying to release Evolve lock.");
                    }
                }
            }
        }

        private void ExecuteMigration(MigrationScript migration, DatabaseHelper db)
        {
            Check.NotNull(migration, nameof(migration));
            Check.NotNull(db, nameof(db));

            var stopWatch = new Stopwatch();
            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadataTableName);

            try
            {
                stopWatch.Start();
                foreach (var statement in db.SqlStatementBuilder.LoadSqlStatements(migration, Placeholders))
                {
                    if (statement.MustExecuteInTransaction)
                    {
                        db.WrappedConnection.TryBeginTransaction();
                    }
                    else
                    {
                        db.WrappedConnection.TryCommit();
                    }

                    db.WrappedConnection.ExecuteNonQuery(statement.Sql, CommandTimeout);
                }

                stopWatch.Stop();
                metadata.SaveMigration(migration, success: true, stopWatch.Elapsed);
                db.WrappedConnection.TryCommit();

                _log($"Successfully {(TransactionMode == TransactionKind.CommitEach ? "applied" : "executed")} migration {migration.Name} in {stopWatch.ElapsedMilliseconds} ms.");
                TotalTimeElapsedInMs += stopWatch.ElapsedMilliseconds;
                NbMigration++;
                AppliedMigrations.Add(migration.Name);
            }
            catch (Exception ex)
            {
                stopWatch.Stop();
                TotalTimeElapsedInMs += stopWatch.ElapsedMilliseconds;
                db.WrappedConnection.TryRollback();
                
                if (TransactionMode == TransactionKind.CommitEach)
                {
                    metadata.SaveMigration(migration, success: false, stopWatch.Elapsed);
                }
                else
                { // When a TransactionScope is used, current transaction is aborted, commands are ignored until end of transaction block
                    LogRollbackAppliedMigration();
                }

                throw new EvolveException($"Error executing script: {migration.Name} after {stopWatch.ElapsedMilliseconds} ms.", ex);
            }
        }

        private void SkipMigration(MigrationScript migration, DatabaseHelper db)
        {
            Check.NotNull(migration, nameof(migration));
            Check.NotNull(db, nameof(db));

            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadataTableName);
            metadata.SaveMigration(migration, success: true);
            _log($"Mark migration {migration.Name} as applied.");
        }

        private void LogRollbackAppliedMigration()
        {
            foreach (string appliedMigration in new Stack<string>(AppliedMigrations))
            {
                _log($"Rollback migration {appliedMigration}.");
            }
            AppliedMigrations.Clear();
        }

        private DatabaseHelper InitiateDatabaseConnection()
        {
            var evolveCnn = new WrappedConnection(_userCnn).Validate();
            var db = DatabaseHelperFactory.GetDatabaseHelper(DBMS, evolveCnn);

            if (Schemas is null || Schemas.Count() == 0)
            { // If no schema declared, get the one associated to the datasource connection
                string? currentSchema = db.GetCurrentSchemaName();
                if (string.IsNullOrEmpty(currentSchema))
                {
                    throw new EvolveConfigurationException("No schema found. At least one schema must be configured either " +
                        "via the Evolve.Schemas option, the Evolve.MetadataTableSchema option or the datasource connection.");
                }

                Schemas = new List<string> { currentSchema };
            }

            if (Command is not CommandOptions.Info and not CommandOptions.Validate)
            {
                _log("Evolve initialized.");
            }

            return db;
        }

        private void WaitForApplicationLock(DatabaseHelper db)
        {
            while (true)
            {
                if (db.TryAcquireApplicationLock())
                {
                    break;
                }

                _log("Cannot acquire Evolve application lock. Another migration is running.");
                Thread.Sleep(TimeSpan.FromSeconds(3));
            }
        }

        private void WaitForMetadataTableLock(DatabaseHelper db)
        {
            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadataTableName);

            while (true)
            {
                if (metadata.TryLock())
                {
                    break;
                }

                _log("Cannot acquire Evolve table lock. Another migration is running.");
                Thread.Sleep(TimeSpan.FromSeconds(3));
            }
        }

        private void ManageSchemas(DatabaseHelper db)
        {
            Check.NotNull(db, nameof(db));

            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadataTableName);

            foreach (var schemaName in FindSchemas())
            {
                var schema = db.GetSchema(schemaName);

                if (!schema.IsExists())
                {
                    _log($"Schema {schemaName} does not exist.");

                    // Create new schema
                    schema.Create();
                    metadata.Save(MetadataType.NewSchema, "0", $"Create new schema: {schemaName}.", schemaName);
                    _log($"Schema {schemaName} created.");
                }
                else if (schema.IsEmpty() && !metadata.IsEmptySchemaMetadataExists(schemaName))
                {
                    // Mark schema as empty in the metadata table
                    metadata.Save(MetadataType.EmptySchema, "0", $"Empty schema found: {schemaName}.", schemaName);

                    _log($"Mark schema {schemaName} as empty.");
                }
            }
        }

        private void ManageStartVersion(DatabaseHelper db)
        {
            if (StartVersion == null || StartVersion == MigrationVersion.MinVersion)
            { // StartVersion parameter undefined
                return;
            }

            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadataTableName);
            var currentStartVersion = metadata.FindStartVersion();

            if (currentStartVersion == StartVersion)
            { // The StartVersion parameter has already been applied
                return;
            }

            if (currentStartVersion != MigrationVersion.MinVersion)
            { // Metadatatable StartVersion found and do not match the StartVersion parameter
                throw new EvolveConfigurationException($"The database has already been flagged with a StartVersion ({currentStartVersion}). Only one StartVersion parameter is allowed.");
            }

            if (metadata.GetAllAppliedMigration().Any())
            { // At least one migration has already been applied, StartVersion parameter not allowed anymore
                throw new EvolveConfigurationException("Use of the StartVersion parameter is not allowed when migrations have already been applied.");
            }

            // Apply StartVersion parameter
            metadata.Save(MetadataType.StartVersion, StartVersion.Label, $"Skip migrations until version {StartVersion.Label} excluded.", $"StartVersion = {StartVersion.Label}");
        }

        private void ValidateAndRepairMetadata(DatabaseHelper db)
        {
            Check.NotNull(db, nameof(db));

            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadataTableName);
            if (!metadata.IsExists())
            { // Nothing to validate
                _log("No metadata found.");
                return;
            }

            var appliedMigrations = metadata.GetAllAppliedMigration();
            if (appliedMigrations.Count() == 0)
            { // Nothing to validate
                _log("No metadata found.");
                return;
            }

            var lastAppliedVersion = metadata.FindLastAppliedVersion();
            var startVersion = metadata.FindStartVersion();
            var scripts = MigrationLoader.GetMigrations()
                                         .SkipWhile(x => x.Version < startVersion)
                                         .TakeWhile(x => x.Version <= lastAppliedVersion); // Keep scripts between first and last applied migration

            foreach (var script in scripts)
            { // Search script in the applied migrations
                var appliedMigration = appliedMigrations.SingleOrDefault(x => x.Version == script.Version);
                if (appliedMigration is null)
                { // Script not found
                    if (OutOfOrder)
                    { // Out of order migration allowed
                        continue;
                    }
                    else
                    { // Validation error
                        throw new EvolveValidationException($"Validation failed: Out of order pending migration found: {script.Name}. Use OutOfOrder option if you want to execute it.");
                    }
                }

                try
                { // Script found, verify checksum
                    script.ValidateChecksum(appliedMigration.Checksum);
                }
                catch
                { // Validation error
                    if (Command == CommandOptions.Repair)
                    { // Repair by updating checksum
                        metadata.UpdateChecksum(appliedMigration.Id, script.CalculateChecksum());
                        NbReparation++;

                        _log($"Checksum fixed for migration: {script.Name}.");
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            _log("Metadata validated.");
        }

        internal IEnumerable<string> FindSchemas()
        {
            return new List<string>().Union(new List<string> { MetadataTableSchema ?? string.Empty })
                                     .Union(Schemas ?? new List<string>())
                                     .Where(s => !s.IsNullOrWhiteSpace())
                                     .Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
}