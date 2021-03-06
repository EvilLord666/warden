﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Warden.Core.Exceptions;
using Warden.Core.Utils;
using Warden.Properties;
using Warden.Windows;
using static Warden.Core.WardenProcess;

namespace Warden.Core
{

    public class WardenOptions
    {
        /// <summary>
        /// If set to true, Warden will watch for the exit event of the host process and kill all 
        /// monitored processes as it shutsdown.
        /// </summary>
        public bool CleanOnExit { get; set; }

        /// <summary>
        /// If set to true, when WardenProcess.Kill is called it will kill the entire process tree.
        /// </summary>
        public bool DeepKill { get; set; }

        /// <summary>
        /// If set to true, Warden will read the portable executable file headers for the binary.
        /// </summary>
        public bool ReadFileHeaders { get; set; }

        /// <summary>
        /// If set to true, Warden will kill processes via Process.Kill instead of Tskill
        /// </summary>
        public bool UseLegacyKill { get; set; }
    }

    public static class WardenManager
    {
        private static ManagementEventWatcher _processStartEvent;
        private static ManagementEventWatcher _processStopEvent;

        public static ConcurrentDictionary<Guid, WardenProcess> ManagedProcesses = new ConcurrentDictionary<Guid, WardenProcess>();




        /// <summary>
        ///     Creates the Warden service which monitors processes on the computer.
        /// </summary>
        /// <param name="options"></param>
        public static void Initialize(WardenOptions options)
        {
            if (!Api.IsAdmin())
            {
                throw new WardenManageException(Resources.Exception_No_Admin);
            }
            Options = options ?? throw new WardenManageException(Resources.Exception_No_Options);
            try
            {

                ShutdownUtils.RegisterEvents();
                _processStartEvent =
                    new ManagementEventWatcher(new WqlEventQuery {EventClassName = "Win32_ProcessStartTrace"});
                _processStopEvent =
                    new ManagementEventWatcher(new WqlEventQuery {EventClassName = "Win32_ProcessStopTrace"});
                _processStartEvent.EventArrived += ProcessStarted;
                _processStopEvent.EventArrived += ProcessStopped;
                _processStartEvent.Start();
                _processStopEvent.Start();
                Initialized = true;
            }
            catch (Exception ex)
            {
                throw new WardenException(ex.Message, ex);
            }
        }

        public static bool Initialized { get; private set; }

        public static WardenOptions Options { get; private set ; }

        /// <summary>
        ///     Flushes a top level process.
        /// </summary>
        /// <param name="processId"></param>
        public static void Flush(int processId)
        {
            try
            {
                var key = ManagedProcesses.FirstOrDefault(x => x.Value.Id == processId).Key;
                ManagedProcesses.TryRemove(key, out _);
            }
            catch
            {
                //
            }
        }

        /// <summary>
        ///     Fired when a process dies.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void ProcessStopped(object sender, EventArrivedEventArgs e)
        {
            var processName = e.NewEvent.Properties["ProcessName"].Value.ToString();
            var processId = int.Parse(e.NewEvent.Properties["ProcessID"].Value.ToString());
            HandleStoppedProcess(processId);
        }

        /// <summary>
        ///     Attempt to update the state of the process if its found in a tree.
        /// </summary>
        /// <param name="processId"></param>
        private static void HandleStoppedProcess(int processId)
        {
            Parallel.ForEach(ManagedProcesses.Values, managed =>
            {
                if (managed.Id == processId)
                {
                    managed.UpdateState(ProcessState.Dead);
                    return;
                }
                var child = FindChildById(processId, managed.Children);
                child?.UpdateState(ProcessState.Dead);
            });
        }

        /// <summary>
        ///     Shutdown the Warden service
        /// </summary>
        public static void Stop()
        {
            _processStartEvent?.Stop();
            _processStopEvent?.Stop();
            if (Options.CleanOnExit)
            {
                Parallel.ForEach(ManagedProcesses.Values, managed =>
                {
                    managed.Kill();
                });
            }
            ManagedProcesses.Clear();
        }
        /// <summary>
        /// Uri launches when done asynchronously are stored with a large process id
        /// We then loop over our stored tree and see if the newly created process matches the target of our async launch
        /// </summary>
        /// <param name="processName"></param>
        /// <param name="processId"></param>
        private static void PreProcessing(string processName, int processId)
        {
            //needed for uri promises
            Parallel.ForEach(ManagedProcesses.ToArray(), kvp =>
            {
                var process = kvp.Value;
                if (process.Id < 999999)
                {
                    return;
                }
                var newProcesWithoutExt = Path.GetFileNameWithoutExtension(processName);
                //Some games from Blizzard have sub executables, so while we look for "HeroesOfTheStorm" it might launch "HeroesOfTheStorm_x64"
                //So we find the most common occurrences in the string now 
                StringUtils.LongestCommonSubstring(process.Name, newProcesWithoutExt, out var subName);
                if (string.IsNullOrWhiteSpace(subName))
                {
                    return;
                }
                if (!process.Name.ToLower().RemoveWhitespace().Equals(subName, StringComparison.CurrentCultureIgnoreCase))
                {
                    return;
                }
                lock (process)
                {
                    ManagedProcesses[kvp.Key].Id = processId;
                    ManagedProcesses[kvp.Key].Name = newProcesWithoutExt;
                    ManagedProcesses[kvp.Key].Path = ProcessUtils.GetProcessPath(processId);
                    ManagedProcesses[kvp.Key].Arguments = ProcessUtils.GetCommandLine(processId);
                    ManagedProcesses[kvp.Key]?.FoundCallback?.Invoke(true);
                };
            });
        }
        /// <summary>
        ///     Detects when a new process launches on the PC, because of URI promises we will also try and update a root managed
        ///     process if
        ///     it is found to be starting up.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void ProcessStarted(object sender, EventArrivedEventArgs e)
        {
            var processName = e.NewEvent.Properties["ProcessName"].Value.ToString();
            var processId = int.Parse(e.NewEvent.Properties["ProcessID"].Value.ToString());
            var processParent = int.Parse(e.NewEvent.Properties["ParentProcessID"].Value.ToString());
            PreProcessing(processName, processId);
            HandleNewProcess(processName, processId, processParent);
        }

     

        /// <summary>
        ///     Attempts to add a new process as a child inside a tree if its parent is present.
        /// </summary>
        /// <param name="processName"></param>
        /// <param name="processId"></param>
        /// <param name="processParent"></param>
        private static void HandleNewProcess(string processName, int processId, int processParent)
        {
            Parallel.ForEach(ManagedProcesses.Values, managed =>
            {
                if (managed.Id == processParent)
                {
                    var childProcess = CreateProcessFromId(processName, managed.Id, processId, managed.Filters);
                    if (!childProcess.IsFiltered() && managed.AddChild(childProcess))
                    {
                        managed.InvokeProcessAdd(new ProcessAddedEventArgs
                        {
                            Name = processName,
                            Id = processId,
                            ParentId = managed.Id
                        });

                        return;
                    }
                }
                var child = FindChildById(processParent, managed.Children);
                if (child == null)
                {

                    return;
                }
                var grandChild = CreateProcessFromId(processName, child.Id, processId, child.Filters);
                if (!grandChild.IsFiltered() && child.AddChild(grandChild))
                {
                    managed.InvokeProcessAdd(new ProcessAddedEventArgs
                    {
                        Name = processName,
                        Id = processId,
                        ParentId = child.Id
                    });
                }
            });
        }
    }
}
