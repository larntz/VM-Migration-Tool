using System;
using System.Windows.Data;

namespace Migration_Tool_UI
{
    public class StateIdConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
            object parameter, System.Globalization.CultureInfo culture)
        {
            switch(value)
            {
                case (int)GridRow.STATEID.Running:
                    return "Running";
                case (int)GridRow.STATEID.Error:
                    return "Error";
                case (int)GridRow.STATEID.Success:
                    return "Success";
                case (int)GridRow.STATEID.Skipped:
                    return "Skipped";
                default:
                    return "Waiting";
                
            }
            
            //return Char.ToUpper(value.ToString()[0]) + value.ToString().Substring(1);
        }

        public object ConvertBack(object value, Type targetType,
            object parameter, System.Globalization.CultureInfo culture)
        {
            switch (value)
            {
                case "running":
                    return (int)GridRow.STATEID.Running;
                case "error":
                    return (int)GridRow.STATEID.Error;
                case "success":
                    return (int)GridRow.STATEID.Success;
                case "skipped":
                    return (int)GridRow.STATEID.Skipped;
                default:
                    return (int)GridRow.STATEID.Waiting;
            }
            //return null;
        }
    }
}
