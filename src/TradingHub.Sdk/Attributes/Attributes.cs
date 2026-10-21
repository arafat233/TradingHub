namespace TradingHub.Attributes
{
    /// <summary>Shows the property only when another property has one of the given values.</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
    public sealed class VisibleWhenAttribute : Attribute
    {
        public VisibleWhenAttribute(string propertyName, params object[] values)
        {
            PropertyName = propertyName;
            Values = values;
        }

        public string PropertyName { get; }
        public object[] Values { get; }
    }

    /// <summary>Link to the indicator's help.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HelpLinkAttribute : Attribute
    {
        public HelpLinkAttribute(string link)
        {
            Link = link;
        }

        public HelpLinkAttribute(string link, string culture) : this(link)
        {
        }

        public string Link { get; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class FeatureIdAttribute : Attribute
    {
        public FeatureIdAttribute(string id)
        {
            Id = id;
        }

        public string Id { get; }
    }

    /// <summary>Tab of the property editor the property appears in.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class TabAttribute : Attribute
    {
        public string TabName { get; set; }
        public int TabOrder { get; set; }
        public Type ResourceType { get; set; }

        public string GetTabName()
        {
            if (ResourceType != null && TabName != null)
            {
                var p = ResourceType.GetProperty(TabName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (p?.GetValue(null) is string s) return s;
            }

            return TabName;
        }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class ExpandableObjectAttribute : Attribute
    {
    }
}

namespace TradingHub.Attributes.Editors
{
    /// <summary>Indicates that a nested object is shown expanded in the editor.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class IsExpandedAttribute : Attribute
    {
    }

    public enum NumericEditorTypes
    {
        Spinner,
        TrackBar,
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class NumericEditorAttribute : Attribute
    {
        public NumericEditorAttribute(NumericEditorTypes type, double min, double max)
        {
            Type = type;
            Min = min;
            Max = max;
        }

        public NumericEditorAttribute(double min, double max) : this(NumericEditorTypes.Spinner, min, max)
        {
        }

        public NumericEditorTypes Type { get; }
        public double Min { get; }
        public double Max { get; }
        public double Step { get; set; } = 1;
        public int DecimalPlaces { get; set; }
        public string DisplayFormat { get; set; }
    }

    public enum PostValueModes
    {
        Immediately,
        Delayed,
        OnLostFocus,
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class PostValueModeAttribute : Attribute
    {
        public PostValueModeAttribute(PostValueModes mode)
        {
            Mode = mode;
        }

        public PostValueModes Mode { get; }
        public int DelayMilliseconds { get; set; }
    }

    public enum MaskTypes
    {
        None,
        Numeric,
        DateTime,
        DateTimeAdvancingCaret,
        RegEx,
        Regular,
        Simple,
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class MaskAttribute : Attribute
    {
        public MaskAttribute(MaskTypes type, string mask)
        {
            Type = type;
            Mask = mask;
        }

        public MaskTypes Type { get; }
        public string Mask { get; }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class SelectFileEditorAttribute : Attribute
    {
        public SelectFileEditorAttribute()
        {
        }

        public SelectFileEditorAttribute(string filter)
        {
            Filter = filter;
        }

        public SelectFileEditorAttribute(Environment.SpecialFolder initialFolder)
        {
            InitialFolder = initialFolder;
        }

        public Environment.SpecialFolder InitialFolder { get; set; } = Environment.SpecialFolder.MyDocuments;
        public string Filter { get; set; }
        public bool IsTextEditable { get; set; } = true;
        public bool Multiselect { get; set; }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class ComboBoxEditorAttribute : Attribute
    {
        public ComboBoxEditorAttribute(Type sourceType, string sourceMember = null)
        {
            SourceType = sourceType;
            SourceMember = sourceMember;
        }

        public Type SourceType { get; }
        public string SourceMember { get; }
        public string DisplayMember { get; set; }
        public string ValueMember { get; set; }
        public bool SelectItemWithNullValue { get; set; }
    }
}
