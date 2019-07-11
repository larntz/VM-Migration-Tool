using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NLog;
using VMware.Vim;

namespace MToolVapiClient
{
    public class MigrationTask : MigrationVM
    {
        public string EntityName { get; set; }
        public string DescriptionId { get; set; }
        public string UserName { get; set; }
        public string Result { get; set; }
    }

    public class MigrationVMDetail
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        public MigrationVMDetail(MigrationVM SourceVM, VAPIConnection VClient)
        {
            var config = new NLog.Config.LoggingConfiguration();

            // Targets where to log to: File and Console
            var logfile = new NLog.Targets.FileTarget("logfile") { FileName = "MToolVapiClient.log", Layout = "${longdate} | ${level:uppercase=true:padding=-5:fixedLength=true} | ${logger:padding=-35:fixedLength=true} | ${message}" };
            var logconsole = new NLog.Targets.ConsoleTarget("logconsole") { Layout = "${longdate} | ${level:uppercase=true:padding=-5:fixedLength=true} | ${logger:padding=-35:fixedLength=true} | ${message}" };

            // Rules for mapping loggers to targets            
            config.AddRule(LogLevel.Trace, LogLevel.Fatal, logconsole);
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, logfile);

            // Apply config           
            NLog.LogManager.Configuration = config;

            try
            { 
                SourceVirtualMachine = (VirtualMachine)VClient.GetViewByName<VirtualMachine>(SourceVM.Name);
                Logger.Info("{0}: Source VM {0} :: {1}", SourceVirtualMachine.Name, SourceVirtualMachine.MoRef);
                SourceHostSystem = (HostSystem)VClient.GetViewByRef<HostSystem>(SourceVirtualMachine.Runtime.Host);
                Logger.Info("{0}: Source Host {1} :: {2}", SourceVirtualMachine.Name, SourceHostSystem.Name, SourceHostSystem.MoRef);
                SourceClusterComputeResource = (ClusterComputeResource)VClient.GetViewByRef<ClusterComputeResource>(SourceHostSystem.Parent);
                Logger.Info("{0}: Source Cluster {1} :: {2}", SourceVirtualMachine.Name, SourceClusterComputeResource.Name, SourceClusterComputeResource.MoRef);
                SourceDatastore = new List<Datastore>();
                foreach (var datastore in SourceVirtualMachine.Datastore)
                {                    
                    var datastoreView = (Datastore)VClient.GetViewByRef<Datastore>(datastore);
                    Logger.Info("{0}: Source Datastore {1} :: {2}", SourceVirtualMachine.Name, datastoreView.Name, datastoreView.MoRef);
                    SourceDatastore.Add(datastoreView);
                    var datastoreParent = VClient.GetViewByRef<StoragePod>(datastoreView.Parent);
                    if (datastoreParent != null)
                    {
                        SourceStoragePod = (StoragePod)datastoreParent;
                        Logger.Info("{0}: Source StoragePod {1} :: {2}", SourceVirtualMachine.Name, SourceStoragePod.Name, SourceStoragePod.MoRef);
                    }
                    Logger.Info("{0}: Total VM Datastores = {1}", SourceVirtualMachine.Name, SourceDatastore.Count());
                    
                }

                EntityViewBase temporaryViewObject;
                temporaryViewObject = VClient.GetViewByName<ClusterComputeResource>(SourceVM.DestinationCompute);
                if (temporaryViewObject != null)
                {
                    DestinationClusterComputeResource = (ClusterComputeResource)temporaryViewObject;
                }
                else
                {
                    temporaryViewObject = VClient.GetViewByName<HostSystem>(SourceVM.DestinationCompute);
                    if (temporaryViewObject != null)
                    {
                        DestinationIsComputeCluster = false;
                        DestinationHostSystem = (HostSystem)temporaryViewObject;
                        DestinationClusterComputeResource = (ClusterComputeResource)VClient.GetViewByRef<ClusterComputeResource>(DestinationHostSystem.Parent);
                    }
                }
                Logger.Info("{0}: Destination Cluster {1} :: {2}", SourceVirtualMachine.Name, DestinationClusterComputeResource.Name, DestinationClusterComputeResource.MoRef);
                Logger.Info("{0}: Destination HostSystem {1} :: {2}", SourceVirtualMachine.Name, DestinationHostSystem?.Name, DestinationHostSystem?.MoRef);

                temporaryViewObject = VClient.GetViewByName<StoragePod>(SourceVM.DestinationStorage);
                if(temporaryViewObject != null)
                {
                    DestinationStoragePod = (StoragePod)VClient.GetViewByName<StoragePod>(SourceVM.DestinationStorage);
                    Logger.Info("{0}: Destination StoragePod {1} :: {2}", SourceVirtualMachine.Name, DestinationStoragePod.Name, DestinationStoragePod.MoRef);

                }
                else
                {
                    DestinationDatastore = (Datastore)VClient.GetViewByName<Datastore>(SourceVM.DestinationStorage);
                    Logger.Info("{0}: Destination Datastore {1} :: {2}", SourceVirtualMachine.Name, DestinationDatastore.Name, DestinationDatastore.MoRef);
                }
            }
            catch (Exception e)
            {
                Logger.Error("EXCEPTION: {0}", e.Message);
                Logger.Error("EXCEPTION: {0}", e.StackTrace);
            }

        }

        public bool Verified
        {
            get
            {
                bool datastoreVerified = false;
                if (DestinationDatastore == null)
                {
                    if (SourceStoragePod.Name != DestinationStoragePod.Name)
                    {
                        Logger.Trace("Comparing StoragePod {0} != {1}", SourceStoragePod.Name, DestinationStoragePod.Name);
                        datastoreVerified = true;
                        MigrateStorage = true;
                    }
                }
                else
                {
                    foreach (var datastore in SourceDatastore)
                        if (datastore.Name != DestinationDatastore.Name)
                        {
                            Logger.Trace("Compating Datastore {0} != {1}", datastore.Name, DestinationDatastore.Name);
                            datastoreVerified = true;
                            MigrateStorage = true;
                        }
                }

                bool computeVerfied = false;
                if (DestinationHostSystem == null)
                {
                    if (SourceClusterComputeResource.Name != DestinationClusterComputeResource.Name)
                    {
                        Logger.Trace("Comparing Clusters {0} != {1}", SourceClusterComputeResource.Name, DestinationClusterComputeResource.Name);
                        computeVerfied = true;
                        MigrateCompute = true;
                    }
                }
                else if (SourceHostSystem.Name != DestinationHostSystem.Name)
                {
                    Logger.Trace("Comparing Hosts {0} != {1}", SourceHostSystem.Name, DestinationHostSystem.Name);
                    computeVerfied = true;
                    MigrateCompute = true;
                }

                return (datastoreVerified || computeVerfied);
            }
        }
        public bool DestinationIsComputeCluster = true;
        public bool MigrateCompute = false;
        public bool MigrateStorage = false;
        public VirtualMachine SourceVirtualMachine { get; set; }
        public ClusterComputeResource SourceClusterComputeResource { get; set; }
        public ClusterComputeResource DestinationClusterComputeResource { get; set; }
        public HostSystem SourceHostSystem { get; set; }
        public HostSystem DestinationHostSystem { get; set; }
        public List<Datastore> SourceDatastore { get; set; }
        public Datastore DestinationDatastore { get; set; }
        public StoragePod SourceStoragePod { get; set; }
        public StoragePod DestinationStoragePod { get; set; }        

    }

    public class MigrationVM
    {
        private string name = string.Empty;
        public string Name
        {
            get
            {
                return name.ToUpper();
            }
            set
            {
                if (name != value)
                    name = value;
            }
        }
        private string destinationCompute;
        public string DestinationCompute
        {
            get
            {
                return destinationCompute.ToUpper();
            }
            set
            {
                if (destinationCompute != value)
                    destinationCompute = value;
            }
        }
        private string destinationStorage;
        public string DestinationStorage
        {
            get
            {
                return destinationStorage.ToUpper();
            }
            set
            {
                if (destinationStorage != value)
                    destinationStorage = value;
            }
        }
        public string State { get; set; }
        public string StateReason { get; set; }
        public int Progress { get; set; }
        public string MoRef { get; set; }
        public DateTime? Start { get; set; }
        public DateTime? Finish { get; set; }
    }

}
