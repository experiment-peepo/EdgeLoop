using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace EdgeLoop.Controls
{
    /// <summary>
    /// One setting: a name and description on the left, its control (the content) on the right, or
    /// below with <see cref="IsStacked"/> for wide controls. Screen readers read the name as the
    /// control's label. Styled by SettingRowStyle in Themes/Settings.xaml.
    /// </summary>
    public class SettingRow : ContentControl
    {
        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingRow), new PropertyMetadata(null, (d, e) => ((SettingRow)d).LabelContent()));

        public string Header
        {
            get => (string)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

        public string Description
        {
            get => (string)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        public static readonly DependencyProperty NoteProperty =
            DependencyProperty.Register(nameof(Note), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

        /// <summary>A small tag after the name, e.g. "New videos only".</summary>
        public string Note
        {
            get => (string)GetValue(NoteProperty);
            set => SetValue(NoteProperty, value);
        }

        public static readonly DependencyProperty IsStackedProperty =
            DependencyProperty.Register(nameof(IsStacked), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));

        /// <summary>Puts the control under the text, full width (text boxes, lists).</summary>
        public bool IsStacked
        {
            get => (bool)GetValue(IsStackedProperty);
            set => SetValue(IsStackedProperty, value);
        }

        public SettingRow()
        {
            SetResourceReference(StyleProperty, "SettingRowStyle");
        }

        protected override void OnContentChanged(object oldContent, object newContent)
        {
            base.OnContentChanged(oldContent, newContent);
            LabelContent();
        }

        private void LabelContent()
        {
            if (Content is DependencyObject control && string.IsNullOrEmpty(AutomationProperties.GetName(control)) && !string.IsNullOrEmpty(Header))
                AutomationProperties.SetName(control, Header);
        }
    }
}
