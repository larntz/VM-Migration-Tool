using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using VMware.Vim;


namespace MToolVapiClient
{    
    public class MigrationVM 
    {
        public string Name { get; set; }
        public string DestinationHost { get; set; }
        public string DestinationDatastore { get; set; }
        public string State { get; set; }
        public int Progress { get; set; }
        public string MoRef { get; set; }
        public DateTime? Start { get; set; }
        public DateTime? Finish { get; set; }
    }
    public class MigrationTask : MigrationVM 
    {
        public string EntityName { get; set; }
        public string DescriptionId { get; set; }
        public string UserName { get; set; }
        public string Result { get; set; }
     }
    public class VAPIConnection
    {
        readonly VimClient vClient = new VimClientImpl();

        public readonly int MAX_MIGRATIONS = 2;
        public VAPIConnection()
        {
            try
            {
                var serverInfo = File.ReadAllLines("../../../Assets/server.txt");
                vClient.IgnoreServerCertificateErrors = true;
                Trace.WriteLine(serverInfo[0], "VCENTER URL");
                vClient.Connect(serverInfo[0]);
                vClient.Login(serverInfo[1], serverInfo[2]);
            }
            catch (Exception e)
            {
                Trace.WriteLine(e.Message, "Exception in VAPIConnection() constructor");
                Trace.WriteLine(e.StackTrace, "Exception in VAPIConnection() constructor");
            }
        }

        public MigrationTask GetTask(string MoRef)
        {
            var thetask = (VMware.Vim.Task)vClient.GetView(new ManagedObjectReference(MoRef), null);

            MigrationTask tTask = new MigrationTask();
            tTask.MoRef = MoRef;
            tTask.EntityName = thetask.Info.EntityName;
            tTask.State = thetask.Info.State.ToString();
            tTask.UserName = ((TaskReasonUser)thetask.Info.Reason).UserName;

            if (thetask.Info.Progress == null)
            {
                if (tTask.State == "success")
                    tTask.Progress = 100;
                else if (tTask.State == "waiting")
                    tTask.Progress = 0;                
            }
            else
                tTask.Progress = (int)thetask.Info.Progress;

            if (thetask.Info.Name != null)
                tTask.Name = thetask.Info.Name;

            tTask.DescriptionId = thetask.Info.DescriptionId;
            tTask.Start = thetask.Info.StartTime;
            tTask.Finish = thetask.Info.CompleteTime;

            return tTask;
        }
        public string MigrateVirtualMachine(MigrationVM virtualMachine)
        {
            VirtualMachineRelocateSpec vmRelocSpec = GetRelocationSpec(virtualMachine);
            if (vmRelocSpec != null)
                return GetVirtualMachine(virtualMachine.Name)
                    .RelocateVM_Task(vmRelocSpec, VirtualMachineMovePriority.highPriority).ToString();

            Trace.WriteLine("MigrateVirtualMachine(): returning null");
            return null;
        }
        private VirtualMachine GetVirtualMachine(string name)
        {
            NameValueCollection filter = new NameValueCollection();
            filter.Add("Name", name);

            return (VirtualMachine) vClient.FindEntityView(typeof(VirtualMachine), null, filter, null);
        }
        private VirtualMachineRelocateSpec GetRelocationSpec(MigrationVM virtualMachine)
        {
            Trace.WriteLine(virtualMachine.Name, "Setting up RelocationSpec for VM");
            Regex storagePodRegex = new Regex(@"group");

            VirtualMachine targetVM = GetVirtualMachine(virtualMachine.Name);
            if (targetVM == null)
            {
                Trace.WriteLine(virtualMachine.Name, "GetRelocateionSPec(): VM NOT FOUND");
                return null;
            }
            ManagedObjectReference targetDatastore = GetDatastoreMoRef(virtualMachine.DestinationDatastore);
                        
            PlacementSpec placementSpec = new PlacementSpec();
            placementSpec.Vm = targetVM.MoRef;
            placementSpec.Priority = VirtualMachineMovePriority.highPriority;
            Match match = storagePodRegex.Match(targetDatastore.Value);
            if(match.Success)
                placementSpec.StoragePods = new[] { targetDatastore };
            else
                placementSpec.Datastores = new[] { targetDatastore };

            HostSystem targetHost = GetHost(virtualMachine.DestinationHost);
            ClusterComputeResource targetCluster;
            if (targetHost != null)
            {
                targetCluster = GetCluster(targetHost.Parent);
                placementSpec.Hosts = new[] { targetHost.MoRef };
            }
            else
            {
                targetCluster = GetCluster(virtualMachine.DestinationHost);
                if (targetCluster == null)
                {
                    return null;
                }
                placementSpec.Hosts = targetCluster.Host;
            }
            ManagedObjectReference[] targetNewtorks = GetTargetNetworks(targetVM.Network, placementSpec.Hosts);

            PlacementResult placementResult = targetCluster.PlaceVm(placementSpec);
            if (placementResult.DrsFault == null)
            {                
                return (VirtualMachineRelocateSpec) ((PlacementAction)placementResult.Recommendations[0].Action[0]).RelocateSpec;
            }

            Trace.WriteLine(placementResult.DrsFault.Reason, "DRS FAULT");
            foreach(var faultsByVm in placementResult.DrsFault.FaultsByVm)
                foreach(var fault in faultsByVm.Fault)
                    Trace.WriteLine(fault.LocalizedMessage, targetVM.Name + ": DRS FAULT");

            return null;
        }
        private ManagedObjectReference GetDatastoreMoRef(string name)
        {
            NameValueCollection filter = new NameValueCollection();
            ManagedObjectReference targetDatastore;

            filter.Add("Name", name);
            targetDatastore = vClient.FindEntityView(typeof(StoragePod), null, filter, null)?.MoRef;
            if (targetDatastore != null)
            {
                return targetDatastore;
            }
            else
            {
                targetDatastore = vClient.FindEntityView(typeof(Datastore), null, filter, null)?.MoRef;
                if (targetDatastore != null)
                {
                    return targetDatastore;
                }
            }
            return null;
        }
        private HostSystem GetHost(string name)
        {
            NameValueCollection filter = new NameValueCollection();
            filter.Add("Name", name);
            return (HostSystem) vClient.FindEntityView(typeof(HostSystem), null, filter, null);
        }
        private HostSystem GetHost(ManagedObjectReference moRef)
        {
            return (HostSystem)vClient.GetView(moRef, null);
        }
        private ClusterComputeResource GetCluster(string name)
        {
            NameValueCollection filter = new NameValueCollection();
            filter.Add("Name", name);
            return (ClusterComputeResource)vClient.FindEntityView(typeof(ClusterComputeResource), null, filter, null);
        }
        private ClusterComputeResource GetCluster(ManagedObjectReference clusterMoRef)
        {
            return (ClusterComputeResource)vClient.GetView(clusterMoRef, null);
        }
        private ManagedObjectReference[] GetTargetNetworks(ManagedObjectReference[] networks, ManagedObjectReference[] hosts)
        {
            Regex networkNetworkRegex = new Regex(@"Network-network-\d+");
            Regex networkDVPRegex = new Regex(@"DistributedVirtualPortgroup-dvportgroup-\d+");
            foreach(var nwMoRef in networks)
            {                
                Trace.WriteLine(nwMoRef, "Network MoRef");                
                if(networkNetworkRegex.Match(nwMoRef.ToString()).Success)
                {
                    // we've got ourselves an old fashioned virtual network
                    var network = (Network) vClient.GetView(nwMoRef, null);
                    HostPortGroup[] portgroups = GetHost(network.Host[0]).Config.Network.Portgroup;
                    foreach (var pg in portgroups)
                    {
                        Trace.WriteLine(pg.Spec.VlanId, pg.Spec.Name + ": Network Vlan");
                    }
                    Trace.WriteLine(network.Name, "Network Name");
                }
                else if (networkDVPRegex.Match(nwMoRef.ToString()).Success)
                {
                    // we've got ourselves a fancy pants distributed portgroup
                    var network = (DistributedVirtualPortgroup)vClient.GetView(nwMoRef, null);
                    Trace.WriteLine(((VmwareDistributedVirtualSwitchVlanIdSpec)((VMwareDVSPortSetting)network.Config.DefaultPortConfig).Vlan).VlanId, network.Name + ": DVSPortgroup Vlan");
                    
                    Trace.WriteLine(network.Name, "DVSPortgroup Name");
                }                
            }
            return new[] { new ManagedObjectReference() };
        }

    }
}
