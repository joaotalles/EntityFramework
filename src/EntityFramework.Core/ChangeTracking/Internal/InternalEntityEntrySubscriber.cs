// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.ComponentModel;
using System.Linq;
using JetBrains.Annotations;
using Microsoft.Data.Entity.Metadata;

namespace Microsoft.Data.Entity.ChangeTracking.Internal
{
    public class InternalEntityEntrySubscriber
    {
        private readonly InternalEntityEntryNotifier _notifier;

        public InternalEntityEntrySubscriber([NotNull] InternalEntityEntryNotifier notifier)
        {
            _notifier = notifier;
        }

        public virtual InternalEntityEntry SnapshotAndSubscribe([NotNull] InternalEntityEntry entry)
        {
            var entityType = entry.EntityType;

            if (entityType.UseEagerSnapshots)
            {
                entry.OriginalValues.TakeSnapshot();
                entry.RelationshipsSnapshot.TakeSnapshot();
            }
            else
            {
                foreach (var navigation in entityType.Navigations.Where(n => n.IsNonNotifyingCollection(entry)))
                {
                    entry.RelationshipsSnapshot.TakeSnapshot(navigation);
                }
            }

            var changing = entry.Entity as INotifyPropertyChanging;
            if (changing != null)
            {
                changing.PropertyChanging += (s, e) =>
                    {
                        var property = TryGetPropertyBase(entityType, e.PropertyName);
                        if (property != null)
                        {
                            _notifier.PropertyChanging(entry, property);
                        }
                    };
            }

            var changed = entry.Entity as INotifyPropertyChanged;
            if (changed != null)
            {
                changed.PropertyChanged += (s, e) =>
                    {
                        var property = TryGetPropertyBase(entityType, e.PropertyName);
                        if (property != null)
                        {
                            _notifier.PropertyChanged(entry, property);
                        }
                    };
            }

            return entry;
        }

        // TODO: Consider optimizing/consolidating property/navigation lookup
        // Issue #635
        private static IPropertyBase TryGetPropertyBase(IEntityType entityType, string propertyName)
            => (IPropertyBase)entityType.TryGetProperty(propertyName)
               ?? entityType.Navigations.FirstOrDefault(n => n.Name == propertyName);
    }
}
