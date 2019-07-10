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
        private string _MigrationCSVFile = String.Empty;
        private readonly VAPIConnection vapiConnection;        
        private readonly DispatcherTimer _Timer;
        
        public GridList MigrationGridList;        
        
        public MainWindow()
        {
            InitializeComponent();
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
                        MessageBoxResult exceptionBox = MessageBox.Show(e.Source + " Says:\n\n" + e.Message, "Problem Migrating " + migrationVM.Name.ToUpper(), MessageBoxButton.OK);
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
                    Trace.WriteLine(migrationVM.Name, "RunMigration(): MigrateVirtualMachine returned null MoRef.");
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
                    Trace.WriteLine(migrationVM.Name, "RunMigration(): Skipped VM.");
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

                    Thread.Sleep(1000);                   
                    
                } while (running);

                return vapiConnection.GetTask(migrationVM.MoRef);
            });

            UpdateMigrationGridListItem(result);            
        }
        private void UpdateMigrationGridListItem(MigrationTask migrationTask)
        {
            // If (MoRef == null) something went wrong. Try update this item by name and exit the function.
            if (migrationTask.MoRef == null)
            {   
                var itemName = MigrationGridList.FirstOrDefault(x => x.Name == migrationTask.EntityName);
                itemName.State = migrationTask.State;
                itemName.StateReason = migrationTask.StateReason;
                itemName.Progress = migrationTask.Progress;
                itemName.Start = migrationTask.Start;
                itemName.Finish = DateTime.Now;
                return;
            }

            var item = MigrationGridList.FirstOrDefault(x => x.MoRef == migrationTask.MoRef);
            if (item != null)
            {
                if (migrationTask.State != null)
                    item.State = migrationTask.State;
                item.StateReason = migrationTask.StateReason;
                item.Progress = migrationTask.Progress;
                item.Start = migrationTask.Start;
                item.Finish = migrationTask.Finish;
            }

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
            sbyte hostIndex = -1;
            sbyte datastoreIndex = -1;

            MigrationGridList.Clear();

            if (!File.Exists(_MigrationCSVFile))
            {
                Trace.WriteLine("File {} does not exist", _MigrationCSVFile);
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
                            hostIndex = (sbyte)Array.IndexOf(columns, col);
                    }
                    if (nameIndex != -1 && hostIndex != -1 && datastoreIndex != -1)
                        needIndexes = false;
                    //Trace.WriteLine("Name index = {0}", nameIndex.ToString());
                    //Trace.WriteLine("Host index = {0}", hostIndex.ToString());
                    //Trace.WriteLine("Datastore index = {0}", datastoreIndex.ToString());
                }
                else
                {
                    GridRow vm = new GridRow();
                    vm.Name = columns[nameIndex].TrimStart('"').TrimEnd('"');
                    vm.DestinationStorage = columns[datastoreIndex].TrimStart('"').TrimEnd('"');
                    vm.DestinationCompute = columns[hostIndex].TrimStart('"').TrimEnd('"');
                    vm.State = "waiting";
                    vm.Progress = 0;
                    MigrationGridList.Add(vm);
                    Trace.WriteLine("migrationList Count", MigrationGridList.Count.ToString());
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
