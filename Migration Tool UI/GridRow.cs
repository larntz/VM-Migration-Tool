using System;
using System.ComponentModel;
namespace Migration_Tool_UI
{
    // Needed for PropertyChangedEventHandler in class GridRow
    sealed class CallerMemberNameAttribute : Attribute { }
    public class GridRow : MToolVapiClient.MigrationVM, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        // This method is called by the Set accessor of each property.  
        // The CallerMemberName attribute that is applied to the optional propertyName  
        // parameter causes the property name of the caller to be substituted as an argument.  
        // https://docs.microsoft.com/en-us/dotnet/api/system.componentmodel.inotifypropertychanged?view=netframework-4.8
        private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // TODO finish this for sorting...
        public enum STATEID : int {Running, Waiting, Error, Success, Skipped }
        public int StateId
        {
            get
            {
                switch (state)
                {
                    case "running":
                        return (int)STATEID.Running;
                    case "error":
                        return (int)STATEID.Error;
                    case "success":
                        return (int)STATEID.Success;
                    case "skipped":
                        return (int)STATEID.Skipped;
                    default:
                        return (int)STATEID.Waiting;
                }
            }            
        }

        private int progress = 0;
        public new int Progress
        {
            get
            {
                return progress;
            }
            set
            {
                if (value != progress)
                {
                    progress = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private string stateReason = String.Empty;
        public new string StateReason
        {
            get
            {
                return stateReason;
            }
            set
            {
                if (value != stateReason)
                {
                    stateReason = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private string state = String.Empty;
        public new string State
        {
            get
            {
                return state;
            }
            set
            {
                if (value != state)
                {
                    state = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private DateTime? start = null;
        public new DateTime? Start
        {
            get
            {
                return start;
            }
            set
            {
                if (value != start)
                {
                    start = value;
                    NotifyPropertyChanged();
                }
            }
        }


        public TimeSpan? MigrationDuration
        {
            get
            {
                if (start.HasValue && finish.HasValue)
                {
                    long sticks = start.Value.Ticks / TimeSpan.TicksPerSecond;
                    DateTime s = new DateTime(sticks * TimeSpan.TicksPerSecond);
                    long fticks = finish.Value.Ticks / TimeSpan.TicksPerSecond;
                    DateTime f = new DateTime(fticks * TimeSpan.TicksPerSecond);
                    return (f - s);
                }
                else
                    return null;
            }

        }

        private DateTime? finish = null;
        public new DateTime? Finish
        {
            get
            {
                return finish;
            }
            set
            {
                if (value != finish)
                {
                    finish = value;
                    NotifyPropertyChanged();
                }
            }
        }

    }
}
