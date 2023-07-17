using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace CalculationManager.Presenters
{
    public class ChangeDetector : IDisposable
    {
        private readonly ChangeDetector _Parent = null;
        private readonly string _PropertyName = null;
        private readonly Dictionary<object, ChangeDetector> _subDetectorsMap = null;


        public event PropertyChangedEventHandler ChangeDetected;

        public virtual bool NotifyOnChange { get; set; } = true;

        public object TrackedObject { get; }


        public ChangeDetector(INotifyPropertyChanged model) : this(model, null, null) { }

        public ChangeDetector(IBindingList list) : this(list, null, null) { }

        private ChangeDetector(object trackable)
        {
            TrackedObject = trackable;
        }

        private ChangeDetector(
            INotifyPropertyChanged trackable,
            string propertyName,
            ChangeDetector parentDetector
        ) : this((object)trackable)
        {
            this._Parent = parentDetector;
            this._PropertyName = propertyName;
            this._subDetectorsMap = this.StartTracking(trackable, !this.IsListItemDetector());
        }

        // TODO: refactor both constuctors(the one that takes a IBindingList and
        // the one that takes an INotifyPropertyChanged) as they have similar implementation.

        private ChangeDetector(
            IBindingList bindingList,
            string propertyName,
            ChangeDetector parentDetector
        ) : this((object)bindingList)
        {
            this._Parent = parentDetector;
            this._PropertyName = propertyName;
            this._subDetectorsMap = this.StartTracking(bindingList, !this.IsListItemDetector());
        }

        private ChangeDetector(
            object trackable,
            string propertyName,
            ChangeDetector parentDetector
        ) : this(trackable)
        {
            this._Parent = parentDetector;
            this._PropertyName = propertyName;
            this._subDetectorsMap = new Dictionary<object, ChangeDetector>();

            var propertiesDetectors = this.StartTracking((INotifyPropertyChanged)trackable);
            var listItemsDetectors = this.StartTracking((IBindingList)trackable);

            foreach (var kvp in propertiesDetectors.Concat(listItemsDetectors))
            {
                this._subDetectorsMap.Add(kvp.Key, kvp.Value);
            }
        }


        private Dictionary<object, ChangeDetector> StartTracking(
            INotifyPropertyChanged trackable,
            bool subscribeToSrcEvents = true)
        {
            if (subscribeToSrcEvents)
            {
                trackable.PropertyChanged += this.ReactToChangeDetection;
            }

            var detectingBranches = GenerateDetectingHierarchies(trackable);

            foreach (var detector in detectingBranches.Values)
            {
                if (detector != null)
                {
                    detector.ChangeDetected += this.ReactToChangeDetection;
                }
            }

            return detectingBranches;
        }

        private Dictionary<object, ChangeDetector> StartTracking(
            IBindingList bindingList,
            bool subscribeToSrcEvents = true)
        {
            if (subscribeToSrcEvents)
            {
                bindingList.ListChanged += this.ReactToChangeDetection;
            }

            var detectingBranches = GenerateDetectingHierarchies(bindingList);

            foreach (var detector in detectingBranches.Values)
            {
                if (detector != null)
                {
                    detector.ChangeDetected += this.ReactToChangeDetection;
                }
            }

            return detectingBranches;
        }

        private Dictionary<object, ChangeDetector> GenerateDetectingHierarchies(
            INotifyPropertyChanged model)
        {
            var detectorsMap = model.GetType().GetProperties()
                .Where((prop) => IsCompoundingType(prop.PropertyType))
                .Select((property) => {
                    object propertyValue = property.GetValue(model);
                    ChangeDetector propertyMutationDetector 
                        = propertyValue is null || IsEndlessRecursionLoop(this, propertyValue)
                        ? null
                        : CreateChildDetector(propertyValue, property.Name, this);
                    return new { PropertyName = property.Name, Detector = propertyMutationDetector };
                }).ToDictionary(
                    keySelector: (obj) => (object)obj.PropertyName,
                    elementSelector: (obj) => obj.Detector
                );

            return detectorsMap;
        }

        private Dictionary<object, ChangeDetector> GenerateDetectingHierarchies(
            IBindingList list)
        {
            var detectorsMap = new Dictionary<object, ChangeDetector>();

            foreach (var item in list)
            {
                if (!IsCompoundingType(item.GetType()) || IsEndlessRecursionLoop(this, item))
                {
                    continue;
                }

                ChangeDetector itemChangeDetector = CreateChildDetector(item, null, this);
                detectorsMap.Add(item, itemChangeDetector);
            }

            return detectorsMap;
        }


        public void Dispose()
        {
            UnsubscribeFromModelChanges();
            
            foreach (var kvp in _subDetectorsMap)
            {
                if (kvp.Value is null) continue;
                kvp.Value.ChangeDetected -= this.ReactToChangeDetection;
                kvp.Value.Dispose();
            }
        }

        private void ReactToChangeDetection(object sender, EventArgs e)
        {
            if (NotifyOnChange)
            {
                this.ForwardPropertyChangedEvent(sender, e);
            }

            if (sender is ChangeDetector) return;

            if (e is PropertyChangedEventArgs propertyChangedArgs)
            {
                bool isCompoundingProperty = this._subDetectorsMap
                    .TryGetValue(propertyChangedArgs.PropertyName, out var oldDetector);

                if (isCompoundingProperty)
                {
                    if (oldDetector != null)
                    {
                        oldDetector.ChangeDetected -= this.ReactToChangeDetection;
                        oldDetector.Dispose();
                    }

                    this.AssignDetectorForProperty(propertyChangedArgs.PropertyName);
                }
            }
            else if (e is ListChangedEventArgs listChangedArgs)
            {
                var list = ((IBindingList)this.TrackedObject);

                switch (listChangedArgs.ListChangedType)
                {
                    case ListChangedType.ItemAdded:
                        var newItem = list[listChangedArgs.NewIndex];
                        
                        if (!this._subDetectorsMap.ContainsKey(newItem) &&
                            IsCompoundingType(newItem.GetType()) &&
                            IsEndlessRecursionLoop(this, newItem) == false)
                        {
                            ChangeDetector itemChangeDetector = CreateChildDetector(newItem, null, this);
                            itemChangeDetector.ChangeDetected += this.ReactToChangeDetection;
                            this._subDetectorsMap.Add(newItem, itemChangeDetector);
                        }
                        break;

                    case ListChangedType.ItemDeleted:
                        var deletedItem = FindMissingItem();
                        if (deletedItem != null)
                        {
                            ChangeDetector itemDetector = this._subDetectorsMap[deletedItem];
                            itemDetector.ChangeDetected -= this.ReactToChangeDetection;
                            this._subDetectorsMap.Remove(deletedItem);
                            itemDetector.Dispose();
                        }
                        break;
                    case ListChangedType.Reset:
                        ClearAllListItemDetectors();
                        break;
                }
            }
        }

        private void ForwardPropertyChangedEvent(object sender, EventArgs e)
        {
            string path = _PropertyName ?? string.Empty;

            if (e is PropertyChangedEventArgs propEventArgs)
            {
                path = CombinePropertyNameToPath(path, propEventArgs.PropertyName);
            }
            else if (e is ListChangedEventArgs listEventArgs
                && listEventArgs.ListChangedType == ListChangedType.ItemChanged)
            {
                path += CombineListItemAndPropertyToPath(
                    listEventArgs.NewIndex,
                    listEventArgs.PropertyDescriptor.Name);
            }

            ChangeDetected?.Invoke(this, new PropertyChangedEventArgs(path));
        }  

        private void AssignDetectorForProperty(string propertyName)
        {
            object propertyValue = TrackedObject
                .GetType()
                .GetProperty(propertyName)
                .GetValue(TrackedObject);

            if (propertyValue is null ||
                propertyValue is INotifyPropertyChanged == false ||
                propertyValue is IBindingList == false)
            {
                return;
            }

            ChangeDetector newChangeDetector = CreateChildDetector(propertyValue, propertyName, this);
            _subDetectorsMap[propertyName] = newChangeDetector;
            newChangeDetector.ChangeDetected += this.ReactToChangeDetection;
        }

        private object FindMissingItem()
        {
            var trackedList = (IBindingList)this.TrackedObject;
            var missingItems = new List<object>();

            var listItemDetectorsPairs = this._subDetectorsMap
                .Where((kvp) => kvp.Key is string == false)
                .ToArray();

            foreach (var pair in listItemDetectorsPairs)
            {
                if (trackedList.Contains(pair.Key) == false)
                {
                    missingItems.Add(pair.Key);
                }
            }

            if (missingItems.Count > 1)
            {
                throw new Exception(
                    "Multiple items found when there should have been at most one. " +
                    "Most probably because of concurrential causes.");
            }

            return missingItems.SingleOrDefault();
        }

        private void ClearAllListItemDetectors()
        {
            var listItemDetectorsPairs = this._subDetectorsMap
                .Where((kvp) => kvp.Key is string == false)
                .ToArray();

            foreach (var pair in listItemDetectorsPairs)
            {
                this._subDetectorsMap.Remove(pair.Key);
                pair.Value?.Dispose();
            }
        }


        private static ChangeDetector CreateChildDetector(
            object propertyValue,
            string propertyName,
            ChangeDetector parentDetector)
        {
            var properyChangedTrackable = propertyValue as INotifyPropertyChanged;
            var listChangedTrackable = propertyValue as IBindingList;

            if (properyChangedTrackable != null && listChangedTrackable != null)
            {
                return new ChangeDetector(propertyValue, propertyName, parentDetector);
            }
            else if (properyChangedTrackable != null)
            {
                return new ChangeDetector(properyChangedTrackable, propertyName, parentDetector);
            }
            else if (listChangedTrackable != null)
            {
                return new ChangeDetector(listChangedTrackable, propertyName, parentDetector);
            }

            return null;
        }

        private void UnsubscribeFromModelChanges()
        {
            if (TrackedObject is INotifyPropertyChanged notifier)
            {
                notifier.PropertyChanged -= ReactToChangeDetection;
            }
            if (TrackedObject is IBindingList bindingList)
            {
                bindingList.ListChanged -= ReactToChangeDetection;
            }
        }

        private static bool IsCompoundingType(Type type)
        {
            return type == typeof(object)
                || typeof(INotifyPropertyChanged).IsAssignableFrom(type)
                || typeof(IBindingList).IsAssignableFrom(type);
        }

        private static bool IsEndlessRecursionLoop(
            ChangeDetector changeDetector,
            object trackableObject)
        {
            var currentDetector = changeDetector;

            while (currentDetector != null)
            {
                if (Equals(currentDetector.TrackedObject, trackableObject))
                {
                    return true;
                }
                currentDetector = currentDetector._Parent;
            }

            return false;
        }

        private bool IsListItemDetector()
        {
            return !(this._Parent is null) && this._PropertyName is null;
        }

        private static string CombinePropertyNameToPath(string path, string propertyName)
        {
            return (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(propertyName))
                ? string.Join(".", path, propertyName)
                : !string.IsNullOrEmpty(path) ? path
                : !string.IsNullOrEmpty(propertyName) ? propertyName
                : string.Empty;
        }

        private static string CombineListItemAndPropertyToPath(int index, string propertyName)
        {
            return $"[{index}].{propertyName}";
        }

        public override string ToString()
        {
            if (_Parent is null) return base.ToString();

            string path = string.Empty;
            ChangeDetector currentDetector = this;
            ChangeDetector previousDetector = null;

            while (currentDetector != null)
            {
                if (currentDetector.IsListItemDetector())
                {
                    var ownerList = (IBindingList)currentDetector._Parent.TrackedObject;
                    var index = ownerList.IndexOf(currentDetector.TrackedObject);
                    path = CombinePropertyNameToPath($"[{index}]", path);
                }
                else if (currentDetector.TrackedObject is IBindingList
                    && (previousDetector?.IsListItemDetector() ?? false))
                {
                    path = (currentDetector._PropertyName ?? string.Empty) + path;
                }
                else
                {
                    path = CombinePropertyNameToPath(currentDetector._PropertyName, path);
                }

                previousDetector = currentDetector;
                currentDetector = currentDetector._Parent;
            }

            return $"{nameof(ChangeDetector)}[\">{path}\"]";
        }
    }
}