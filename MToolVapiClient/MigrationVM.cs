using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
        public MigrationVMDetail(MigrationVM SourceVM, VAPIConnection VClient)
        {
            try
            { 
                SourceVirtualMachine = (VirtualMachine)VClient.GetViewByName<VirtualMachine>(SourceVM.Name);
                SourceHostSystem = (HostSystem)VClient.GetViewByRef<HostSystem>(SourceVirtualMachine.Runtime.Host);
                Trace.WriteLine(SourceHostSystem.Name, "HOST");
                SourceClusterComputeResource = (ClusterComputeResource)VClient.GetViewByRef<ClusterComputeResource>(SourceHostSystem.Parent);
                Trace.WriteLine(SourceClusterComputeResource.Name, "CLUSTER");
                SourceDatastore = new List<Datastore>();
                foreach (var datastore in SourceVirtualMachine.Datastore)
                {                    
                    var datastoreView = (Datastore)VClient.GetViewByRef<Datastore>(datastore);
                    Trace.WriteLine(datastoreView.Name, "DATASTORE");
                    SourceDatastore.Add(datastoreView);
                    var datastoreParent = VClient.GetViewByRef<StoragePod>(datastoreView.Parent);
                    if (datastoreParent != null)
                    {
                        SourceStoragePod = (StoragePod)datastoreParent;
                        Trace.WriteLine(SourceStoragePod.Name, "STORAGEPOD");
                    }
                    Trace.WriteLine(SourceDatastore.Count(), "DATASTORE");
                    
                }

                EntityViewBase temporaryViewObject;
                temporaryViewObject = VClient.GetViewByName<HostSystem>(SourceVM.DestinationCompute);
                if (temporaryViewObject != null)
                    DestinationHostSystem = (HostSystem)temporaryViewObject;
                Trace.WriteLine(DestinationHostSystem?.Name, "DHOST");

                temporaryViewObject = VClient.GetViewByName<ClusterComputeResource>(SourceVM.DestinationCompute);
                DestinationClusterComputeResource = (ClusterComputeResource)temporaryViewObject ?? (ClusterComputeResource)VClient.GetViewByRef<ClusterComputeResource>(DestinationHostSystem.Parent);
                Trace.WriteLine(DestinationClusterComputeResource.Name, "DCLUSTER");

                temporaryViewObject = VClient.GetViewByName<Datastore>(SourceVM.DestinationStorage);
                if(temporaryViewObject != null)
                {
                    DestinationDatastore = (Datastore)temporaryViewObject;
                    Trace.WriteLine(DestinationDatastore.Name, "DDATASTORE");
                }
                else
                {
                    DestinationStoragePod = (StoragePod)VClient.GetViewByName<StoragePod>(SourceVM.DestinationStorage);
                    Trace.WriteLine(DestinationStoragePod.Name, "DSTORAGEPOD");
                }
            }
            catch (VimException e)
            {
                Trace.WriteLine(e.Message);
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
                        datastoreVerified = true;
                    }
                }
                else
                {
                    foreach (var datastore in SourceDatastore)
                        if (datastore.Name != DestinationDatastore.Name)
                        {
                            datastoreVerified = true;
                        }
                }

                bool computeVerfied = false;
                if (DestinationHostSystem == null)
                {
                    if (SourceClusterComputeResource.Name != DestinationClusterComputeResource.Name)
                    {
                        computeVerfied = true;
                    }
                }
                else if (SourceHostSystem.Name != DestinationHostSystem.Name)
                {
                    computeVerfied = true;
                }

                return (datastoreVerified || computeVerfied);
            }
        }
                
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
        public string Name { get; set; }
        public string DestinationCompute { get; set; }
        public string DestinationStorage { get; set; }
        public string State { get; set; }
        public string StateReason { get; set; }
        public int Progress { get; set; }
        public string MoRef { get; set; }
        public DateTime? Start { get; set; }
        public DateTime? Finish { get; set; }
    }

}
