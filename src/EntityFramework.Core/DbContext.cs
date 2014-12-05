// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Data.Entity.ChangeTracking;
using Microsoft.Data.Entity.Infrastructure;
using Microsoft.Data.Entity.Internal;
using Microsoft.Data.Entity.Metadata;
using Microsoft.Data.Entity.Storage;
using Microsoft.Data.Entity.Utilities;
using Microsoft.Framework.DependencyInjection;
using Microsoft.Framework.Logging;
using Microsoft.Framework.OptionsModel;

namespace Microsoft.Data.Entity
{
    public class DbContext : IDisposable, IDbContextServices
    {
        private static readonly ThreadSafeDictionaryCache<Type, Type> _optionsTypes = new ThreadSafeDictionaryCache<Type, Type>();

        private readonly LazyRef<DbContextServices> _configuration;
        private readonly LazyRef<ILogger> _logger;
        private readonly LazyRef<DbSetInitializer> _setInitializer;

        private bool _initializing;

        protected DbContext()
        {
            var serviceProvider = DbContextActivator.ServiceProvider;
            var options = GetOptions(serviceProvider);

            InitializeSets(serviceProvider, options);
            _configuration = new LazyRef<DbContextServices>(() => Initialize(serviceProvider, options));
            _logger = new LazyRef<ILogger>(CreateLogger);
            _setInitializer = new LazyRef<DbSetInitializer>(GetSetInitializer);
        }

        public DbContext([NotNull] IServiceProvider serviceProvider)
        {
            Check.NotNull(serviceProvider, "serviceProvider");

            var options = GetOptions(serviceProvider);

            InitializeSets(serviceProvider, options);
            _configuration = new LazyRef<DbContextServices>(
                () => Initialize(serviceProvider, options));

            _logger = new LazyRef<ILogger>(CreateLogger);
            _setInitializer = new LazyRef<DbSetInitializer>(GetSetInitializer);
        }

        private DbContextOptions GetOptions(IServiceProvider serviceProvider)
        {
            if (serviceProvider == null)
            {
                return new DbContextOptions();
            }

            var genericOptions = _optionsTypes.GetOrAdd(GetType(), t => typeof(DbContextOptions<>).MakeGenericType(t));

            var optionsAccessor = (IOptions<DbContextOptions>)serviceProvider.TryGetService(
                typeof(IOptions<>).MakeGenericType(genericOptions));
            if (optionsAccessor != null)
            {
                return optionsAccessor.Options;
            }

            optionsAccessor = serviceProvider.TryGetService<IOptions<DbContextOptions>>();
            if (optionsAccessor != null)
            {
                return optionsAccessor.Options;
            }

            var options = (DbContextOptions)serviceProvider.TryGetService(genericOptions);
            if (options != null)
            {
                return options;
            }

            options = serviceProvider.TryGetService<DbContextOptions>();
            if (options != null)
            {
                return options;
            }

            return new DbContextOptions();
        }

        public DbContext([NotNull] DbContextOptions options)
        {
            Check.NotNull(options, "options");

            var serviceProvider = DbContextActivator.ServiceProvider;

            InitializeSets(serviceProvider, options);
            _configuration = new LazyRef<DbContextServices>(() => Initialize(serviceProvider, options));
            _logger = new LazyRef<ILogger>(CreateLogger);
            _setInitializer = new LazyRef<DbSetInitializer>(GetSetInitializer);
        }

        // TODO: Consider removing this constructor if DbContextOptions should be obtained from serviceProvider
        // Issue #192
        public DbContext([NotNull] IServiceProvider serviceProvider, [NotNull] DbContextOptions options)
        {
            Check.NotNull(serviceProvider, "serviceProvider");
            Check.NotNull(options, "options");

            InitializeSets(serviceProvider, options);
            _configuration = new LazyRef<DbContextServices>(() => Initialize(serviceProvider, options));
            _logger = new LazyRef<ILogger>(CreateLogger);
            _setInitializer = new LazyRef<DbSetInitializer>(GetSetInitializer);
        }

        private ILogger CreateLogger()
        {
            return _configuration.Value.ScopedServiceProvider.GetRequiredServiceChecked<ILoggerFactory>().Create<DbContext>();
        }

        private DbSetInitializer GetSetInitializer()
        {
            return _configuration.Value.ScopedServiceProvider.GetRequiredServiceChecked<DbSetInitializer>();
        }

        private ChangeDetector GetChangeDetector()
        {
            return _configuration.Value.ScopedServiceProvider.GetRequiredServiceChecked<ChangeDetector>();
        }

        private StateManager GetStateManager()
        {
            return _configuration.Value.ScopedServiceProvider.GetRequiredServiceChecked<StateManager>();
        }

        private DbContextServices Initialize(IServiceProvider serviceProvider, DbContextOptions options)
        {
            if (_initializing)
            {
                throw new InvalidOperationException(Strings.RecursiveOnConfiguring);
            }

            try
            {
                _initializing = true;

                options = options.Clone();

                OnConfiguring(options);

                var providerSource = serviceProvider != null
                    ? DbContextServices.ServiceProviderSource.Explicit
                    : DbContextServices.ServiceProviderSource.Implicit;

                serviceProvider = serviceProvider ?? ServiceProviderCache.Instance.GetOrAdd(options);

                var scopedServiceProvider = serviceProvider
                    .GetRequiredServiceChecked<IServiceScopeFactory>()
                    .CreateScope()
                    .ServiceProvider;

                return scopedServiceProvider
                    .GetRequiredServiceChecked<DbContextServices>()
                    .Initialize(scopedServiceProvider, options, this, providerSource);
            }
            finally
            {
                _initializing = false;
            }
        }

        private void InitializeSets(IServiceProvider serviceProvider, DbContextOptions options)
        {
            serviceProvider = serviceProvider ?? ServiceProviderCache.Instance.GetOrAdd(options);

            serviceProvider.GetRequiredServiceChecked<DbSetInitializer>().InitializeSets(this);
        }

        IServiceProvider IDbContextServices.ScopedServiceProvider
        {
            get { return _configuration.Value.ScopedServiceProvider; }
        }

        protected internal virtual void OnConfiguring(DbContextOptions options)
        {
        }

        protected internal virtual void OnModelCreating(ModelBuilder modelBuilder)
        {
        }

        [DebuggerStepThrough]
        public virtual int SaveChanges()
        {
            var stateManager = GetStateManager();

            // TODO: Allow auto-detect changes to be switched off
            // Issue #745
            GetChangeDetector().DetectChanges(stateManager);

            try
            {
                return stateManager.SaveChanges();
            }
            catch (Exception ex)
            {
                _logger.Value.WriteError(
                    new DataStoreErrorLogState(GetType()),
                    ex,
                    (state, exception) =>
                        Strings.LogExceptionDuringSaveChanges(Environment.NewLine, exception));

                throw;
            }
        }

        public virtual async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            var stateManager = GetStateManager();

            // TODO: Allow auto-detect changes to be switched off
            // Issue #745
            GetChangeDetector().DetectChanges(stateManager);

            try
            {
                return await stateManager.SaveChangesAsync(cancellationToken).WithCurrentCulture();
            }
            catch (Exception ex)
            {
                _logger.Value.WriteError(
                    new DataStoreErrorLogState(GetType()),
                    ex,
                    (state, exception) =>
                        Strings.LogExceptionDuringSaveChanges(Environment.NewLine, exception));

                throw;
            }
        }

        public virtual void Dispose()
        {
            if (_configuration.HasValue)
            {
                _configuration.Value.Dispose();
            }
        }

        public virtual EntityEntry<TEntity> Entry<TEntity>([NotNull] TEntity entity)
        {
            Check.NotNull(entity, "entity");

            return new EntityEntry<TEntity>(this, GetStateManager().GetOrCreateEntry(entity));
        }

        public virtual EntityEntry Entry([NotNull] object entity)
        {
            Check.NotNull(entity, "entity");

            return new EntityEntry(this, GetStateManager().GetOrCreateEntry(entity));
        }

        public virtual EntityEntry<TEntity> Add<TEntity>([NotNull] TEntity entity)
        {
            Check.NotNull(entity, "entity");

            return SetEntityState(entity, EntityState.Added);
        }

        public virtual async Task<EntityEntry<TEntity>> AddAsync<TEntity>(
            [NotNull] TEntity entity, CancellationToken cancellationToken = default(CancellationToken))
        {
            Check.NotNull(entity, "entity");

            var entry = Entry(entity);

            await entry.StateEntry
                .SetEntityStateAsync(EntityState.Added, cancellationToken)
                .WithCurrentCulture();

            return entry;
        }

        public virtual EntityEntry<TEntity> Attach<TEntity>([NotNull] TEntity entity)
        {
            Check.NotNull(entity, "entity");

            return SetEntityState(entity, EntityState.Unchanged);
        }

        public virtual EntityEntry<TEntity> Update<TEntity>([NotNull] TEntity entity)
        {
            Check.NotNull(entity, "entity");

            return SetEntityState(entity, EntityState.Modified);
        }

        public virtual EntityEntry<TEntity> Remove<TEntity>([NotNull] TEntity entity)
        {
            Check.NotNull(entity, "entity");

            return SetEntityState(entity, EntityState.Deleted);
        }

        private EntityEntry<TEntity> SetEntityState<TEntity>(TEntity entity, EntityState entityState)
        {
            var entry = Entry(entity);

            entry.State = entityState;

            return entry;
        }

        public virtual EntityEntry Add([NotNull] object entity)
        {
            Check.NotNull(entity, "entity");

            return SetEntityState(entity, EntityState.Added);
        }

        public virtual async Task<EntityEntry> AddAsync(
            [NotNull] object entity, CancellationToken cancellationToken = default(CancellationToken))
        {
            Check.NotNull(entity, "entity");

            var entry = Entry(entity);

            await entry.StateEntry
                .SetEntityStateAsync(EntityState.Added, cancellationToken)
                .WithCurrentCulture();

            return entry;
        }

        public virtual EntityEntry Attach([NotNull] object entity)
        {
            Check.NotNull(entity, "entity");

            return SetEntityState(entity, EntityState.Unchanged);
        }

        public virtual EntityEntry Update([NotNull] object entity)
        {
            Check.NotNull(entity, "entity");

            return SetEntityState(entity, EntityState.Modified);
        }

        public virtual EntityEntry Remove([NotNull] object entity)
        {
            Check.NotNull(entity, "entity");

            return SetEntityState(entity, EntityState.Deleted);
        }

        private EntityEntry SetEntityState(object entity, EntityState entityState)
        {
            var entry = Entry(entity);

            entry.State = entityState;

            return entry;
        }

        public virtual IReadOnlyList<EntityEntry<TEntity>> Add<TEntity>([NotNull] params TEntity[] entities)
        {
            Check.NotNull(entities, "entities");

            return SetEntityStates(entities, EntityState.Added);
        }

        public virtual Task<IReadOnlyList<EntityEntry<TEntity>>> AddAsync<TEntity>([NotNull] params TEntity[] entities)
        {
            Check.NotNull(entities, "entities");

            return AddAsync(entities, default(CancellationToken));
        }

        public virtual async Task<IReadOnlyList<EntityEntry<TEntity>>> AddAsync<TEntity>(
            [NotNull] TEntity[] entities,
            CancellationToken cancellationToken)
        {
            Check.NotNull(entities, "entities");

            var entries = GetOrCreateEntries(entities);

            foreach (var entry in entries)
            {
                await entry.StateEntry
                    .SetEntityStateAsync(EntityState.Added, cancellationToken)
                    .WithCurrentCulture();
            }

            return entries;
        }

        public virtual IReadOnlyList<EntityEntry<TEntity>> Attach<TEntity>([NotNull] params TEntity[] entities)
        {
            Check.NotNull(entities, "entities");

            return SetEntityStates(entities, EntityState.Unchanged);
        }

        public virtual IReadOnlyList<EntityEntry<TEntity>> Update<TEntity>([NotNull] params TEntity[] entities)
        {
            Check.NotNull(entities, "entities");

            return SetEntityStates(entities, EntityState.Modified);
        }

        public virtual IReadOnlyList<EntityEntry<TEntity>> Remove<TEntity>([NotNull] params TEntity[] entities)
        {
            Check.NotNull(entities, "entities");

            return SetEntityStates(entities, EntityState.Deleted);
        }

        private List<EntityEntry<TEntity>> SetEntityStates<TEntity>(TEntity[] entities, EntityState entityState)
        {
            var entries = GetOrCreateEntries(entities);

            foreach (var entry in entries)
            {
                entry.State = entityState;
            }

            return entries;
        }

        private List<EntityEntry<TEntity>> GetOrCreateEntries<TEntity>(IEnumerable<TEntity> entities)
        {
            var stateManager = GetStateManager();

            return entities.Select(e => new EntityEntry<TEntity>(this, stateManager.GetOrCreateEntry(e))).ToList();
        }

        public virtual IReadOnlyList<EntityEntry> Add([NotNull] params object[] entities)
        {
            Check.NotNull(entities, "entities");

            return SetEntityStates(entities, EntityState.Added);
        }

        public virtual Task<IReadOnlyList<EntityEntry>> AddAsync([NotNull] params object[] entities)
        {
            Check.NotNull(entities, "entities");

            return AddAsync(entities, default(CancellationToken));
        }

        public virtual async Task<IReadOnlyList<EntityEntry>> AddAsync(
            [NotNull] object[] entities,
            CancellationToken cancellationToken)
        {
            Check.NotNull(entities, "entities");

            var entries = GetOrCreateEntries(entities);

            foreach (var entry in entries)
            {
                await entry.StateEntry
                    .SetEntityStateAsync(EntityState.Added, cancellationToken)
                    .WithCurrentCulture();
            }

            return entries;
        }

        public virtual IReadOnlyList<EntityEntry> Attach([NotNull] params object[] entities)
        {
            Check.NotNull(entities, "entities");

            return SetEntityStates(entities, EntityState.Unchanged);
        }

        public virtual IReadOnlyList<EntityEntry> Update([NotNull] params object[] entities)
        {
            Check.NotNull(entities, "entities");

            return SetEntityStates(entities, EntityState.Modified);
        }

        public virtual IReadOnlyList<EntityEntry> Remove([NotNull] params object[] entities)
        {
            Check.NotNull(entities, "entities");

            return SetEntityStates(entities, EntityState.Deleted);
        }

        private List<EntityEntry> SetEntityStates(object[] entities, EntityState entityState)
        {
            var entries = GetOrCreateEntries(entities);

            foreach (var entry in entries)
            {
                entry.State = entityState;
            }

            return entries;
        }

        private List<EntityEntry> GetOrCreateEntries(IEnumerable<object> entities)
        {
            var stateManager = GetStateManager();

            return entities.Select(e => new EntityEntry(this, stateManager.GetOrCreateEntry(e))).ToList();
        }

        public virtual Database Database
        {
            get { return _configuration.Value.ScopedServiceProvider.GetRequiredServiceChecked<DbContextService<Database>>().Service; }
        }

        public virtual ChangeTracker ChangeTracker
        {
            get { return _configuration.Value.ScopedServiceProvider.GetRequiredServiceChecked<ChangeTracker>(); }
        }

        public virtual IModel Model
        {
            get { return _configuration.Value.ScopedServiceProvider.GetRequiredServiceChecked<DbContextService<IModel>>().Service; }
        }

        public virtual DbSet<TEntity> Set<TEntity>()
            where TEntity : class
        {
            // Note: Creating sets needs to be fast because it is done eagerly when a context instance
            // is created so we avoid loading metadata to validate the type here.
            return _setInitializer.Value.CreateSet<TEntity>(this);
        }
    }
}