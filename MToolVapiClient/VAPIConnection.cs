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
using NLog;


namespace MToolVapiClient
{    
    public class VAPIConnection
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly VimClient vClient = new VimClientImpl();

        public readonly int MAX_MIGRATIONS = 2;
        public VAPIConnection()
        {
            var config = new NLog.Config.LoggingConfiguration();

            // Targets where to log to: File and Console
            var logfile = new NLog.Targets.FileTarget("logfile") { FileName = "MToolVapiClient.log", Layout= "${longdate} | ${level:uppercase=true:padding=-5:fixedLength=true} | ${logger:padding=-35:fixedLength=true} | ${message}" };
            var logconsole = new NLog.Targets.ConsoleTarget("logconsole") { Layout = "${longdate} | ${level:uppercase=true:padding=-5:fixedLength=true} | ${logger:padding=-35:fixedLength=true} | ${message}" };

            // Rules for mapping loggers to targets            
            config.AddRule(LogLevel.Trace, LogLevel.Fatal, logconsole);
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, logfile);

            // Apply config           
            NLog.LogManager.Configuration = config;

            Logger.Info("Logging Initialized");
            try
            {
                var serverInfo = File.ReadAllLines("../../../Assets/server.txt");
                vClient.IgnoreServerCertificateErrors = true;
                Logger.Info("Connecting to server {0}", serverInfo[0]);
                vClient.Connect(serverInfo[0]);
                vClient.Login(serverInfo[1], serverInfo[2]);
            }
            catch (VimException e)
            {
                Logger.Error("EXCEPTION: {0}", e.Message);
                Logger.Error("EXCEPTION: {0}", e.StackTrace);
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
                Logger.Error("{0}: Skipped: Verify Failed", virtualMachine.Name);
                return "skipped-verifyfailed";
            }

            try { 
                
                VirtualMachineRelocateSpec vmRelocSpec = GetRelocationSpec(vmDetail);
                if (vmRelocSpec != null)
                {
                    Logger.Info("==========================================");
                    Logger.Info("Migrating {0} to {1}{2}", virtualMachine.Name, vmDetail.DestinationClusterComputeResource?.Name, vmDetail.DestinationHostSystem?.Name);
                    Logger.Info("Migrating {0} storage to {1}{2}", virtualMachine.Name, vmDetail.DestinationDatastore?.Name, vmDetail.DestinationStoragePod?.Name);
                    if (vmRelocSpec.DeviceChange.Count() > 0)
                        foreach (var deviceChange in vmRelocSpec.DeviceChange)
                            Logger.Info("Migrating networking to {@0}", deviceChange);
                    Logger.Info("==========================================");

                    return ((VirtualMachine) GetViewByName<VirtualMachine>(virtualMachine.Name))
                            .RelocateVM_Task(vmRelocSpec, VirtualMachineMovePriority.highPriority).ToString();
                }
            }
            catch (Exception e)
            {
                Logger.Error("EXCEPTION: {0}", e.Message);
                Logger.Error("EXCEPTION: {0}", e.StackTrace);
                throw;
            }

            Logger.Trace("return null {@value}", vmDetail);
            return null;
        }

        private VirtualMachineRelocateSpec GetRelocationSpec( MigrationVMDetail vmDetail)
        {            
            Logger.Info("{0}: Setting up RelocationSpec", vmDetail.SourceVirtualMachine.Name);

            try
            {
                PlacementSpec placementSpec = new PlacementSpec();
                placementSpec.Vm = vmDetail.SourceVirtualMachine.MoRef;
                placementSpec.Priority = VirtualMachineMovePriority.highPriority;
                placementSpec.PlacementType = "relocate";
                placementSpec.RelocateSpec = new VirtualMachineRelocateSpec();

                if (vmDetail.MigrateCompute)
                {
                    if (vmDetail.DestinationIsComputeCluster)
                    {
                        //placementSpec.Hosts = vmDetail.DestinationClusterComputeResource.Host;
                        vmDetail.DestinationHostSystem = (HostSystem)GetViewByRef<HostSystem>(vmDetail.DestinationClusterComputeResource.RecommendHostsForVm(vmDetail.SourceVirtualMachine.MoRef, vmDetail.DestinationClusterComputeResource.ResourcePool).FirstOrDefault().Host);
                        placementSpec.RelocateSpec.Host = vmDetail.DestinationHostSystem.MoRef;
                        placementSpec.RelocateSpec.Pool = vmDetail.DestinationClusterComputeResource.ResourcePool;
                        Logger.Trace("{0}: Recomended Host {1}", vmDetail.SourceVirtualMachine.Name, placementSpec.RelocateSpec.Host.ToString());
                    }
                    else
                    {
                        placementSpec.RelocateSpec.Host = vmDetail.DestinationHostSystem.MoRef;
                        placementSpec.RelocateSpec.Pool = vmDetail.DestinationClusterComputeResource.ResourcePool;
                    }

                    var networkDeviceConfigSpecs = UpdateVMNetworkDevices(vmDetail.SourceVirtualMachine, placementSpec.RelocateSpec.Host);
                    foreach (var ndc in networkDeviceConfigSpecs)
                    {
                        Logger.Info("{0}: Network Device Config: {1}", vmDetail.SourceVirtualMachine.Name, ndc.Device.Backing);
                    }

                    placementSpec.RelocateSpec.DeviceChange = networkDeviceConfigSpecs.ToArray();
                    Logger.Trace("{value0}: placmentSpec: {@value1}", vmDetail.SourceVirtualMachine.Name, placementSpec);
                }

                if (vmDetail.MigrateStorage)
                {
                    if (vmDetail.DestinationStoragePod != null)
                    {
                        StoragePlacementSpec storagePlacementSpec = new StoragePlacementSpec
                        {
                            Vm = vmDetail.SourceVirtualMachine.MoRef,
                            Type = "relocate",
                            Priority = VirtualMachineMovePriority.highPriority,
                            PodSelectionSpec = new StorageDrsPodSelectionSpec
                            {
                                StoragePod = vmDetail.DestinationStoragePod.MoRef
                            }
                        };
                        var storageRM = new StorageResourceManager(vClient, vClient.ServiceContent.StorageResourceManager);
                        var storageResult = storageRM.RecommendDatastores(storagePlacementSpec);
                        Logger.Trace("{value0}: storageResult: {@value1}", vmDetail.SourceVirtualMachine.Name, storageResult);
                        Logger.Trace("{0}: StoragePlacementResult: Setting placementSpec.RelocateSpec.Datastore to {@value1}", vmDetail.SourceVirtualMachine.Name, ((StoragePlacementAction)storageResult.Recommendations.FirstOrDefault().Action.FirstOrDefault()).Destination);
                        placementSpec.RelocateSpec.Datastore = ((StoragePlacementAction)storageResult.Recommendations.FirstOrDefault().Action.FirstOrDefault()).Destination;

                        //Logger.Trace("{0}: Setting placementSpec.StoragePods to {1}", vmDetail.SourceVirtualMachine.Name, vmDetail.DestinationStoragePod.MoRef);
                        //placementSpec.StoragePods = new[] { vmDetail.DestinationStoragePod.MoRef };
                    }
                    else
                    {
                        Logger.Trace("{0}: Setting placementSpec.RelocateSpec.Datastore to {1}", vmDetail.SourceVirtualMachine.Name, vmDetail.DestinationDatastore.MoRef);
                        placementSpec.RelocateSpec.Datastore = vmDetail.DestinationDatastore.MoRef;
                    }
                }

                //PlacementResult placementResult = vmDetail.DestinationClusterComputeResource.PlaceVm(placementSpec);
                //if (placementResult.DrsFault == null)
                //{
                //    var recommendedRelocSpec = (VirtualMachineRelocateSpec)((PlacementAction)placementResult.Recommendations[0].Action[0]).RelocateSpec;
                //    Logger.Info("{value0}: recommendedRelocSpec: {@value1}", vmDetail.SourceVirtualMachine.Name, recommendedRelocSpec);
                //    placementSpec.RelocateSpec.Host = recommendedRelocSpec.Host;
                //    Logger.Info("{0}: Recomended Host {1}", vmDetail.SourceVirtualMachine.Name, recommendedRelocSpec.Host.ToString());
                //    placementSpec.RelocateSpec.Datastore = recommendedRelocSpec.Datastore;
                //    Logger.Info("{0}: Recomended Datastore {1}", vmDetail.SourceVirtualMachine.Name, recommendedRelocSpec.Datastore.ToString());
                //    Logger.Trace("{value0}: Final RelocationSpec: {@value1}", vmDetail.SourceVirtualMachine.Name, placementSpec.RelocateSpec);
                //    return placementSpec.RelocateSpec;

                //}
                //else
                //{
                //    Logger.Error("{0}: DRS FAULT: {1}", vmDetail.SourceVirtualMachine.Name, placementResult.DrsFault.Reason);
                //    foreach (var faultsByVm in placementResult.DrsFault.FaultsByVm)
                //        foreach (var fault in faultsByVm.Fault)
                //            Logger.Error("{0}: FaultsByVm: {1}", vmDetail.SourceVirtualMachine.Name, fault.LocalizedMessage);

                
                return placementSpec.RelocateSpec;

                    //}
            }
            catch(Exception e)
            {
                Logger.Error("EXCEPTION: {0}", vmDetail.SourceVirtualMachine.Name);
                Logger.Error("EXCEPTION: {0}", e.Message);
                Logger.Error("EXCEPTION: {0}", e.StackTrace);
                throw;
            }
        }
        private List<VirtualDeviceConfigSpec> UpdateVMNetworkDevices(VirtualMachine virtualMachine, ManagedObjectReference host)
        {            
            List<VirtualDeviceConfigSpec> nicConfigSpecs = new List<VirtualDeviceConfigSpec>();

            var nics = virtualMachine.Config.Hardware.Device.Where(x => x.GetType().IsSubclassOf(typeof(VirtualEthernetCard)));
            foreach(VirtualEthernetCard nic in nics)
            {
                if (nic.Backing is VirtualEthernetCardNetworkBackingInfo)
                    nic.Backing = GetNetworkByVlanId(nic, host);

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
                Logger.Info("{0}: NIC Summary: {1}", virtualMachine.Name, nic.DeviceInfo.Summary);
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
        private VirtualDeviceBackingInfo GetNetworkByVlanId(VirtualEthernetCard nic, ManagedObjectReference host)
        {
            // see if we can find another virtual network on the same vlan with a different name. 
            var networkMoRef = ((VirtualEthernetCardNetworkBackingInfo)nic.Backing).Network;
            var virtualNetwork = (Network)vClient.GetView(networkMoRef, null);
            HostPortGroup portgroup = ((HostSystem)GetViewByRef<HostSystem>(host))
                                                    .Config.Network.Portgroup.FirstOrDefault(x => x.Spec.Name == virtualNetwork.Name);

            if (portgroup == null)
            {
                // Check for other virtual networks on the same vlan.
                int currentPortgroupVlanId = ((HostSystem)GetViewByRef<HostSystem>(((Network)vClient.GetView(networkMoRef, null)).Host.FirstOrDefault()))
                                        .Config.Network.Portgroup.FirstOrDefault(x => x.Spec.Name == virtualNetwork.Name)
                                        .Spec.VlanId;
                HostPortGroup portgroupByVlan = ((HostSystem)GetViewByRef<HostSystem>(host))
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
