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
            {
                try
                {
                    Trace.WriteLine("==========================================");
                    Trace.WriteLine("Attempating to migrate " + virtualMachine.Name.ToUpper());
                    Trace.WriteLine("Moving to host " + vmRelocSpec.Host.ToString());
                    if (vmRelocSpec.DeviceChange.Count() > 0)
                        Trace.WriteLine("Moving to network " + vmRelocSpec.DeviceChange[0].Device.DeviceInfo.Summary);
                    Trace.WriteLine("Moving to datastore" + vmRelocSpec.Datastore.ToString());
                    Trace.WriteLine("==========================================");

                    return GetVirtualMachine(virtualMachine.Name)
                        .RelocateVM_Task(vmRelocSpec, VirtualMachineMovePriority.highPriority).ToString();
                }
                catch (Exception e)
                {
                    Trace.WriteLine(e.Message);
                    Trace.WriteLine(e.StackTrace);
                }
            }

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

            var targetHost = (HostSystem) GetViewByName<HostSystem>(virtualMachine.DestinationHost);
            ClusterComputeResource targetCluster;
            if (targetHost != null)
            {
                targetCluster = (ClusterComputeResource) GetViewByRef<ClusterComputeResource>(targetHost.Parent);
                placementSpec.Hosts = new[] { targetHost.MoRef };
            }
            else
            {
                targetCluster = (ClusterComputeResource) GetViewByName<ClusterComputeResource>(virtualMachine.DestinationHost);
                if (targetCluster == null)
                {
                    return null;
                }
                placementSpec.Hosts = targetCluster.Host;
            }

            var networkDeviceConfigSpecs = UpdateVMNetworkDevices(targetVM, placementSpec.Hosts);
            placementSpec.RelocateSpec = new VirtualMachineRelocateSpec { DeviceChange = networkDeviceConfigSpecs.ToArray() };


            PlacementResult placementResult = targetCluster.PlaceVm(placementSpec);

            if (placementResult.DrsFault == null)
            {
                var recommendedRelocSpec = (VirtualMachineRelocateSpec)((PlacementAction)placementResult.Recommendations[0].Action[0]).RelocateSpec;
                placementSpec.RelocateSpec.Host = recommendedRelocSpec.Host;
                placementSpec.RelocateSpec.Datastore = recommendedRelocSpec.Datastore;
                placementSpec.RelocateSpec.Pool = recommendedRelocSpec.Pool;
                placementSpec.RelocateSpec.Folder = recommendedRelocSpec.Folder;
                return placementSpec.RelocateSpec;
                
            }
            

            Trace.WriteLine(placementResult.DrsFault.Reason, "DRS FAULT");
            foreach(var faultsByVm in placementResult.DrsFault.FaultsByVm)
                foreach(var fault in faultsByVm.Fault)
                    Trace.WriteLine(fault.LocalizedMessage, targetVM.Name + ": DRS FAULT");

            return null;
        }
        private List<VirtualDeviceConfigSpec> UpdateVMNetworkDevices(VirtualMachine virtualMachine, ManagedObjectReference[] hosts)
        {            
            List<VirtualDeviceConfigSpec> nicConfigSpecs = new List<VirtualDeviceConfigSpec>();

            var nics = virtualMachine.Config.Hardware.Device.Where(x => x.GetType().IsSubclassOf(typeof(VirtualEthernetCard)));
            foreach(VirtualEthernetCard nic in nics)
            {
                if (nic.Backing is VirtualEthernetCardNetworkBackingInfo)
                    nic.Backing = GetNetworkByVlanId(nic, hosts);

                if (nic.Backing == null)
                    continue;

                var spec = new VirtualDeviceConfigSpec
                {
                    Operation = VirtualDeviceConfigSpecOperation.edit,
                    Device = nic
                };

                nic.DeviceInfo = new Description
                {
                    Summary = ((VirtualEthernetCardNetworkBackingInfo)nic.Backing).DeviceName,
                    Label = nic.DeviceInfo.Label
                };
                
                Trace.WriteLine(nic.DeviceInfo.Summary, virtualMachine.Name + " NIC Summary");
                nicConfigSpecs.Add(spec);
            }
            return nicConfigSpecs;
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
        private VirtualDeviceBackingInfo GetNetworkByVlanId(VirtualEthernetCard nic, ManagedObjectReference[] hosts)
        {
            // see if we can find another virtual network on the same vlan with a different name. 
            var networkMoRef = ((VirtualEthernetCardNetworkBackingInfo)nic.Backing).Network;
            var virtualNetwork = (Network)vClient.GetView(networkMoRef, null);
            HostPortGroup portgroup = ((HostSystem)GetViewByRef<HostSystem>(hosts.FirstOrDefault()))
                                                    .Config.Network.Portgroup.FirstOrDefault(x => x.Spec.Name == virtualNetwork.Name);

            if (portgroup == null)
            {
                // Check for other virtual networks on the same vlan.
                int currentPortgroupVlanId = ((HostSystem)GetViewByRef<HostSystem>(((Network)vClient.GetView(networkMoRef, null)).Host.FirstOrDefault()))
                                        .Config.Network.Portgroup.FirstOrDefault(x => x.Spec.Name == virtualNetwork.Name)
                                        .Spec.VlanId;
                HostPortGroup portgroupByVlan = ((HostSystem)GetViewByRef<HostSystem>(hosts.FirstOrDefault()))
                                        .Config.Network.Portgroup.FirstOrDefault(x => x.Spec.VlanId == currentPortgroupVlanId);

                if (portgroupByVlan != null)
                {
                    var targetVirtualNetwork = (Network)GetViewByName<Network>(portgroupByVlan.Spec.Name);
                    VirtualEthernetCardNetworkBackingInfo nicBacking = new VirtualEthernetCardNetworkBackingInfo
                    {
                        DeviceName = targetVirtualNetwork.Name,
                        Network = targetVirtualNetwork.MoRef
                    };
                    // we found a virtual network on one of the new target hosts. return that as network backing
                    return nicBacking;
                }
            }

            return null;
        }

        private VirtualDeviceBackingInfo FindNetwork(Network virtualNetwork, ManagedObjectReference[] hosts)
        {   
            // Check for other virtual networks on the same vlan.
            int currentPortgroupVlanId = ((HostSystem) GetViewByRef<HostSystem>(((Network)vClient.GetView(virtualNetwork.MoRef, null)).Host.FirstOrDefault()))
                                    .Config.Network.Portgroup.FirstOrDefault(x => x.Spec.Name == virtualNetwork.Name)
                                    .Spec.VlanId;
            HostPortGroup portgroupByVlan = ((HostSystem) GetViewByRef<HostSystem>(hosts.FirstOrDefault()))
                                    .Config.Network.Portgroup.FirstOrDefault(x => x.Spec.VlanId == currentPortgroupVlanId);
            if (portgroupByVlan != null)
            {
                var targetVirtualNetwork = (Network)GetViewByName<Network>(portgroupByVlan.Spec.Name);
                VirtualEthernetCardNetworkBackingInfo nicBacking = new VirtualEthernetCardNetworkBackingInfo
                {
                    DeviceName = targetVirtualNetwork.Name,
                    Network = targetVirtualNetwork.MoRef
                };
                // we found a virtual network on one of the new target hosts. return that as network backing
                return nicBacking;
            }

            // no luck
            return null;
        }        

        private EntityViewBase GetViewByName<T>(string name)
        {
            NameValueCollection filter = new NameValueCollection();
            filter.Add("Name", name);
            return vClient.FindEntityView(typeof(T), null, filter, null);
        }

        private EntityViewBase GetViewByRef<T>(ManagedObjectReference moRef)
        {
            return (EntityViewBase) vClient.GetView(moRef, null);
        }


    }
}
