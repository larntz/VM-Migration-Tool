using System;
using System.IO;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Diagnostics;
using System.ComponentModel;
using NLog;

using MToolVapiClient;

namespace Migration_Tool_UI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    /// 


    public class GridList : ObservableCollection<GridRow> { }

    public partial class MainWindow : Window
    {
        private static readonly NLog.Logger MigrationLogger = NLog.LogManager.GetCurrentClassLogger();
        private string _MigrationCSVFile = String.Empty;
        private readonly VAPIConnection vapiConnection;        
        private readonly DispatcherTimer _Timer;
        
        public GridList MigrationGridList;        
        
        public MainWindow()
        {
            InitializeComponent();

            var config = new NLog.Config.LoggingConfiguration();

            // Targets where to log to: File and Console
            string logFilePath = "migration-" + DateTime.Now.ToFileTime().ToString() + ".log";
            var logfile = new NLog.Targets.FileTarget("logfile") { FileName = logFilePath, Layout = "${longdate} | ${level:uppercase=true:padding=-5:fixedLength=true} | ${logger:padding=-35:fixedLength=true} | ${message}" };
            var logconsole = new NLog.Targets.ConsoleTarget("logconsole") { Layout = "${longdate} | ${level:uppercase=true:padding=-5:fixedLength=true} | ${logger:padding=-35:fixedLength=true} | ${message}" };

            // Rules for mapping loggers to targets            
            config.AddRule(LogLevel.Trace, LogLevel.Fatal, logconsole);
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, logfile);

            // Apply config           
            NLog.LogManager.Configuration = config;

            MigrationLogger.Info("Migration Logging Initialized");


            MigrationGridList = (GridList)this.Resources["MigrationGridList"];
            vapiConnection = new VAPIConnection();
            _Timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _Timer.Tick += (s, e) =>
            {
                var nextMigration = MigrationGridList.FirstOrDefault(x => x.State == "waiting");
                if ((nextMigration != null) && (MigrationGridList.Where(x => x.State == "running").Count() < vapiConnection.MAX_MIGRATIONS))
                {
                    nextMigration.State = "running";
                    SortGridCollection();
                    RunMigration(nextMigration);
                }
                else if ((MigrationGridList.Where(x => x.State == "running").Count() == 0) && (nextMigration == null))
                {
                    _Timer.Stop();
                    btnSelectFile.IsEnabled = true;
                }
            };            
        }
        private async void RunMigration(GridRow migrationVM)
        {
            var progressHanlder = new Progress<MigrationTask>(value =>
            {
                UpdateMigrationGridListItem(value);
            });

            var migration = progressHanlder as IProgress<MigrationTask>;

            var result = await Task.Run(() =>
            {
                try
                {
                    migrationVM.MoRef = vapiConnection.MigrateVirtualMachine(migrationVM);
                }
                catch (Exception e)
                {
                    // this is horrible and hacky but good enough for now.
                    // polish this! => add error message to MigrationTask for inclusion in datagrid/logs/output?
                    Task.Run(() =>
                    {
                        MessageBoxResult exceptionBox = MessageBox.Show(e.Source + " Says:\n\n" + e.Message + "\n\n" + e.StackTrace, "Problem Migrating " + migrationVM.Name.ToUpper(), MessageBoxButton.OK);
                    });
                    return new MigrationTask
                    {
                        State = "error",
                        Start = DateTime.Now,
                        EntityName = migrationVM.Name,
                        DestinationStorage = migrationVM.DestinationStorage,
                        DestinationCompute = migrationVM.DestinationCompute,
                        Progress = migrationVM.Progress
                    };
                }
                
                if (migrationVM.MoRef == null)
                {
                    MigrationLogger.Error("{0}: MigrateVirtualMachine() returned null", migrationVM.Name);
                    // task failed to start. Return what we know.
                    return new MigrationTask
                    {
                        State = "error",
                        Start = DateTime.Now,
                        EntityName = migrationVM.Name,
                        DestinationStorage = migrationVM.DestinationStorage,
                        DestinationCompute = migrationVM.DestinationCompute,
                        Progress = migrationVM.Progress
                    };
                }
                else if(migrationVM.MoRef == "skipped-verifyfailed")
                {
                    MigrationLogger.Info("{0}: Skipping VM", migrationVM.Name);
                    // task failed to start. Return what we know.
                    return new MigrationTask
                    {
                        State = "skipped",
                        StateReason = "VM is already at destination.",
                        Start = DateTime.Now,
                        EntityName = migrationVM.Name,
                        DestinationStorage = migrationVM.DestinationStorage,
                        DestinationCompute = migrationVM.DestinationCompute,
                        Progress = migrationVM.Progress
                    };
                }

                bool running = true;
                do
                {
                    var thisTask = vapiConnection.GetTask(migrationVM.MoRef);
                    if (migration != null)                    
                        migration.Report(thisTask);

                    if (thisTask.State != "running")
                        running = false;

                    Thread.Sleep(3000);                   
                    
                } while (running);

                return vapiConnection.GetTask(migrationVM.MoRef);
            });

            UpdateMigrationGridListItem(result);            
        }
        private void UpdateMigrationGridListItem(MigrationTask migrationTask)
        {
            // If (MoRef == null) something went wrong. Try update this item by name and exit the function.
            var item = new GridRow();
            if (migrationTask.MoRef == null)
            {
                item = MigrationGridList.FirstOrDefault(x => x.Name == migrationTask.EntityName);
            }
            else
            {
                item = MigrationGridList.FirstOrDefault(x => x.MoRef == migrationTask.MoRef);
            }

            if (migrationTask.State != null)
                item.State = migrationTask.State;
            item.StateReason = migrationTask.StateReason;
            item.Progress = migrationTask.Progress;
            item.Start = migrationTask.Start;
            item.Finish = migrationTask.Finish;
            MigrationLogger.Info("{@value0}", item);

            SortGridCollection();
        }
        private void SortGridCollection()
        {
            ICollectionView migrationGridCollection = CollectionViewSource.GetDefaultView(dgGridList.ItemsSource);
            migrationGridCollection.SortDescriptions.Clear();
            migrationGridCollection.SortDescriptions.Add(new SortDescription("StateId", ListSortDirection.Ascending));
        }
        private void Parse_MigrationCSVFile()
        {
            bool needIndexes = true;
            sbyte nameIndex = -1;
            sbyte computeIndex = -1;
            sbyte datastoreIndex = -1;

            MigrationGridList.Clear();

            if (!File.Exists(_MigrationCSVFile))
            {
                MigrationLogger.Error("{0}: File not found.", _MigrationCSVFile);
                return;
            }

            var lines = File.ReadLines(_MigrationCSVFile, Encoding.UTF8);
            foreach (var line in lines)
            {
                var columns = line.Split(',');
                if (needIndexes)
                {
                    foreach (var col in columns)
                    {
                        if (col.TrimStart('"').TrimEnd('"').ToUpper() == "NAME")
                            nameIndex = (sbyte)Array.IndexOf(columns, col);
                        if (col.TrimStart('"').TrimEnd('"').ToUpper() == "DESTINATIONSTORAGE")
                            datastoreIndex = (sbyte)Array.IndexOf(columns, col);
                        if (col.TrimStart('"').TrimEnd('"').ToUpper() == "DESTINATIONCOMPUTE")
                            computeIndex = (sbyte)Array.IndexOf(columns, col);
                    }
                    if (nameIndex != -1 && computeIndex != -1 && datastoreIndex != -1)
                        needIndexes = false;
                    MigrationLogger.Trace("Reading CSV File: Name index = {0}; DestinationCompute index = {1}; DestinationStorage index = {2}", nameIndex, computeIndex, datastoreIndex);
                }
                else
                {
                    GridRow vm = new GridRow();
                    vm.Name = columns[nameIndex].TrimStart('"').TrimEnd('"');
                    vm.DestinationStorage = columns[datastoreIndex].TrimStart('"').TrimEnd('"');
                    vm.DestinationCompute = columns[computeIndex].TrimStart('"').TrimEnd('"');
                    vm.State = "waiting";
                    vm.Progress = 0;
                    MigrationGridList.Add(vm);
                    MigrationLogger.Trace("{0}: items in migrationList", MigrationGridList.Count);
                }

                btnStartMigration.IsEnabled = true;
            }
        }
        private void SelectFile_Button_Click(object sender, RoutedEventArgs e)
        {
            // Create OpenFileDialog 
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();

            // Set filter for file extension and default file extension 
            dlg.DefaultExt = ".csv";
            dlg.Filter = "Comma Separated Text (*.csv)|*.csv";

            // Display OpenFileDialog by calling ShowDialog method 
            if (dlg.ShowDialog() == true)
            {
                _MigrationCSVFile = dlg.FileName;
                txtSelectedFile.Text = _MigrationCSVFile;
                Parse_MigrationCSVFile();
            }
        }
        private void StartMigration_Button_Click(object sender, RoutedEventArgs e)
        {
            btnStartMigration.IsEnabled = false;
            btnSelectFile.IsEnabled = false;
            _Timer.Start();
        }
    }
}
