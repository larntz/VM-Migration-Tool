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
            catch (VimException e)
            {
                Trace.WriteLine(e.Message, "Exception in VAPIConnection() constructor");
                Trace.WriteLine(e.StackTrace, "Exception in VAPIConnection() constructor");
            }
        }

        public EntityViewBase GetViewByName<T>(string name)
        {
            NameValueCollection filter = new NameValueCollection();
            filter.Add("Name", name);
            return vClient.FindEntityView(typeof(T), null, filter, null);
        }
        public EntityViewBase GetViewByRef<T>(ManagedObjectReference moRef)
        {
            return (EntityViewBase)vClient.GetView(moRef, null);
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
                {
                    tTask.Progress = 100;
                }
                else if (tTask.State == "error")
                {
                    tTask.StateReason = thetask.Info.Error?.LocalizedMessage;
                }
                else if (tTask.State == "waiting")
                {
                    tTask.Progress = 0;
                }
            }
            else
                tTask.Progress = (int)thetask.Info.Progress;

            //if (thetask.Info.Name != null)
            tTask.Name = thetask.Info?.Name;

            tTask.DescriptionId = thetask.Info.DescriptionId;
            tTask.Start = thetask.Info.StartTime;
            tTask.Finish = thetask.Info.CompleteTime;

            return tTask;

            
        }
        public string MigrateVirtualMachine(MigrationVM virtualMachine)
        {
            MigrationVMDetail vmDetail = new MigrationVMDetail(virtualMachine, this);
            if (!vmDetail.Verified)
            {
                return "skipped-verifyfailed";
            }

            try { 
                
                VirtualMachineRelocateSpec vmRelocSpec = GetRelocationSpec(virtualMachine);
                if (vmRelocSpec != null)
                {
 
                        Trace.WriteLine("==========================================");
                        Trace.WriteLine("Attempating to migrate " + virtualMachine.Name.ToUpper());
                        Trace.WriteLine("Moving to host " + vmRelocSpec.Host.ToString());
                        if (vmRelocSpec.DeviceChange.Count() > 0)
                            Trace.WriteLine("Moving to network " + vmRelocSpec.DeviceChange[0].Device.DeviceInfo.Summary);
                        Trace.WriteLine("Moving to datastore " + vmRelocSpec.Datastore.ToString());
                        Trace.WriteLine("==========================================");

                        return ((VirtualMachine) GetViewByName<VirtualMachine>(virtualMachine.Name))
                            .RelocateVM_Task(vmRelocSpec, VirtualMachineMovePriority.highPriority).ToString();
                }
            }
            catch (VimException e)
            {
                Trace.WriteLine("EXCEPTION");
                Trace.WriteLine(e.Source, "EXCEPTION");
                Trace.WriteLine(e.Message, "EXCEPTION");
                Trace.WriteLine(e.StackTrace, "EXCEPTION");
                throw;
            }

            Trace.WriteLine("MigrateVirtualMachine(): returning null");
            return null;
        }

        private VirtualMachineRelocateSpec GetRelocationSpec(MigrationVM virtualMachine)
        {
            Trace.WriteLine(virtualMachine.Name, "Setting up RelocationSpec for VM");
            Regex storagePodRegex = new Regex(@"group");

            var targetVM = (VirtualMachine) GetViewByName<VirtualMachine>(virtualMachine.Name);
            if (targetVM == null)
            {
                Trace.WriteLine(virtualMachine.Name, "GetRelocateionSpec(): VM NOT FOUND");
                return null;
            }
            ManagedObjectReference targetDatastore = GetDatastoreMoRef(virtualMachine.DestinationStorage);
                        
            PlacementSpec placementSpec = new PlacementSpec();
            placementSpec.Vm = targetVM.MoRef;
            placementSpec.Priority = VirtualMachineMovePriority.highPriority;
            placementSpec.PlacementType = "relocate";
            Match match = storagePodRegex.Match(targetDatastore.Value);
            if(match.Success)
                placementSpec.StoragePods = new[] { targetDatastore };
            else
                placementSpec.Datastores = new[] { targetDatastore };

            var targetHost = (HostSystem) GetViewByName<HostSystem>(virtualMachine.DestinationCompute);
            ClusterComputeResource targetCluster;
            if (targetHost != null)
            {
                targetCluster = (ClusterComputeResource) GetViewByRef<ClusterComputeResource>(targetHost.Parent);
                placementSpec.Hosts = new[] { targetHost.MoRef };
            }
            else
            {
                targetCluster = (ClusterComputeResource) GetViewByName<ClusterComputeResource>(virtualMachine.DestinationCompute);
                if (targetCluster == null)
                {
                    return null;
                }
                placementSpec.Hosts = targetCluster.Host;
            }

            var networkDeviceConfigSpecs = UpdateVMNetworkDevices(targetVM, placementSpec.Hosts);
            placementSpec.RelocateSpec = new VirtualMachineRelocateSpec { DeviceChange = networkDeviceConfigSpecs.ToArray() };

            try
            {
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
                foreach (var faultsByVm in placementResult.DrsFault.FaultsByVm)
                    foreach (var fault in faultsByVm.Fault)
                        Trace.WriteLine(fault.LocalizedMessage, targetVM.Name + ": DRS FAULT");
            }
            catch(VimException)
            {
                throw;
            }
            



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
    }
}
