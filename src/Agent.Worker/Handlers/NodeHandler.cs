// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk;
using Agent.Sdk.Knob;
using Microsoft.VisualStudio.Services.Agent.Util;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Linq;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Handlers
{
    [ServiceLocator(Default = typeof(NodeHandler))]
    public interface INodeHandler : IHandler
    {
        // Data can be of these three types: NodeHandlerData, Node10HandlerData and Node16HandlerData
        BaseNodeHandlerData Data { get; set; }
    }

    [ServiceLocator(Default = typeof(NodeHandlerHelper))]
    public interface INodeHandlerHelper
    {
        string[] GetFilteredPossibleNodeFolders(string nodeFolderName, string[] possibleNodeFolders);
        string GetNodeFolderPath(string nodeFolderName, IHostContext hostContext);
        bool IsNodeFolderExist(string nodeFolderName, IHostContext hostContext);
    }

    public class NodeHandlerHelper : INodeHandlerHelper
    {
        public bool IsNodeFolderExist(string nodeFolderName, IHostContext hostContext) => File.Exists(GetNodeFolderPath(nodeFolderName, hostContext));

        public string GetNodeFolderPath(string nodeFolderName, IHostContext hostContext) => Path.Combine(
            hostContext.GetDirectory(WellKnownDirectory.Externals),
            nodeFolderName,
            "bin",
            $"node{IOUtil.ExeExtension}");

        public string[] GetFilteredPossibleNodeFolders(string nodeFolderName, string[] possibleNodeFolders)
        {
            int nodeFolderIndex = Array.IndexOf(possibleNodeFolders, nodeFolderName);

            return nodeFolderIndex >= 0 ?
                possibleNodeFolders.Skip(nodeFolderIndex + 1).ToArray()
                : Array.Empty<string>();
        }
    }

    public sealed class NodeHandler : Handler, INodeHandler
    {
        private readonly INodeHandlerHelper nodeHandlerHelper;
        private const string nodeFolder = "node";
        private const string node10Folder = "node10";
        private const string node16Folder = "node16";
        private const string nodeLTS = node16Folder;
        private const string useNodeKnobLtsKey = "LTS";
        private const string useNodeKnobUpgradeKey = "UPGRADE";
        private string[] possibleNodeFolders = { nodeFolder, node10Folder, node16Folder };
        private static Regex _vstsTaskLibVersionNeedsFix = new Regex("^[0-2]\\.[0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static string[] _extensionsNode6 ={
            "if (process.versions.node && process.versions.node.match(/^5\\./)) {",
            "   String.prototype.startsWith = function (str) {",
            "       return this.slice(0, str.length) == str;",
            "   };",
            "   String.prototype.endsWith = function (str) {",
            "       return this.slice(-str.length) == str;",
            "   };",
            "};",
            "String.prototype.isEqual = function (ignoreCase, str) {",
            "   var str1 = this;",
            "   if (ignoreCase) {",
            "       str1 = str1.toLowerCase();",
            "       str = str.toLowerCase();",
            "       }",
            "   return str1 === str;",
            "};"
        };

        public NodeHandler()
        {
            this.nodeHandlerHelper = new NodeHandlerHelper();
        }

        public NodeHandler(INodeHandlerHelper nodeHandlerHelper)
        {
            this.nodeHandlerHelper = nodeHandlerHelper;
        }

        public BaseNodeHandlerData Data { get; set; }

        public async Task RunAsync()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(Data, nameof(Data));
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(Inputs, nameof(Inputs));
            ArgUtil.Directory(TaskDirectory, nameof(TaskDirectory));

            if (!PlatformUtil.RunningOnWindows)
            {
                // Ensure compat vso-task-lib exist at the root of _work folder
                // This will make vsts-agent work against 2015 RTM/QU1 TFS, since tasks in those version doesn't package with task lib
                // Put the 0.5.5 version vso-task-lib into the root of _work/node_modules folder, so tasks are able to find those lib.
                if (!File.Exists(Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), "node_modules", "vso-task-lib", "package.json")))
                {
                    string vsoTaskLibFromExternal = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Externals), "vso-task-lib");
                    string compatVsoTaskLibInWork = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), "node_modules", "vso-task-lib");
                    IOUtil.CopyDirectory(vsoTaskLibFromExternal, compatVsoTaskLibInWork, ExecutionContext.CancellationToken);
                }
            }

            // Update the env dictionary.
            AddInputsToEnvironment();
            AddEndpointsToEnvironment();
            AddSecureFilesToEnvironment();
            AddVariablesToEnvironment();
            AddTaskVariablesToEnvironment();
            AddPrependPathToEnvironment();

            // Resolve the target script.
            string target = Data.Target;
            ArgUtil.NotNullOrEmpty(target, nameof(target));
            target = Path.Combine(TaskDirectory, target);
            ArgUtil.File(target, nameof(target));

            // Resolve the working directory.
            string workingDirectory = Data.WorkingDirectory;
            if (string.IsNullOrEmpty(workingDirectory))
            {
                workingDirectory = ExecutionContext.Variables.Get(Constants.Variables.System.DefaultWorkingDirectory);
                if (string.IsNullOrEmpty(workingDirectory))
                {
                    workingDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
                }
            }

            // fix vsts-task-lib for node 6.x
            // vsts-task-lib 0.6/0.7/0.8/0.9/2.0-preview implemented String.prototype.startsWith and String.prototype.endsWith since Node 5.x doesn't have them.
            // however the implementation is added in node 6.x, the implementation in vsts-task-lib is different.
            // node 6.x's implementation takes 2 parameters str.endsWith(searchString[, length]) / str.startsWith(searchString[, length])
            // the implementation vsts-task-lib had only takes one parameter str.endsWith(searchString) / str.startsWith(searchString).
            // as long as vsts-task-lib be loaded into memory, it will overwrite the implementation node 6.x has,
            // so any script that use the second parameter (length) will encounter unexpected result.
            // to avoid customer hit this error, we will modify the file (extensions.js) under vsts-task-lib module folder when customer choose to use Node 6.x
            Trace.Info("Inspect node_modules folder, make sure vsts-task-lib doesn't overwrite String.startsWith/endsWith.");
            FixVstsTaskLibModule();

            StepHost.OutputDataReceived += OnDataReceived;
            StepHost.ErrorDataReceived += OnDataReceived;

            string file;
            if (!string.IsNullOrEmpty(ExecutionContext.StepTarget()?.CustomNodePath))
            {
                file = ExecutionContext.StepTarget().CustomNodePath;
            }
            else
            {
                file = GetNodeLocation();

                ExecutionContext.Debug("Using node path: " + file);
            }

            // Format the arguments passed to node.
            // 1) Wrap the script file path in double quotes.
            // 2) Escape double quotes within the script file path. Double-quote is a valid
            // file name character on Linux.
            string arguments = StepHost.ResolvePathForStepHost(StringUtil.Format(@"""{0}""", target.Replace(@"""", @"\""")));
            // Let .NET choose the default, except on Windows.
            Encoding outputEncoding = null;
            if (PlatformUtil.RunningOnWindows)
            {
                // It appears that node.exe outputs UTF8 when not in TTY mode.
                outputEncoding = Encoding.UTF8;
            }

            try
            {
                // Execute the process. Exit code 0 should always be returned.
                // A non-zero exit code indicates infrastructural failure.
                // Task failure should be communicated over STDOUT using ## commands.
                Task step = StepHost.ExecuteAsync(workingDirectory: StepHost.ResolvePathForStepHost(workingDirectory),
                                                  fileName: StepHost.ResolvePathForStepHost(file),
                                                  arguments: arguments,
                                                  environment: Environment,
                                                  requireExitCodeZero: true,
                                                  outputEncoding: outputEncoding,
                                                  killProcessOnCancel: false,
                                                  inheritConsoleHandler: !ExecutionContext.Variables.Retain_Default_Encoding,
                                                  continueAfterCancelProcessTreeKillAttempt: _continueAfterCancelProcessTreeKillAttempt,
                                                  cancellationToken: ExecutionContext.CancellationToken);

                // Wait for either the node exit or force finish through ##vso command
                await System.Threading.Tasks.Task.WhenAny(step, ExecutionContext.ForceCompleted);

                if (ExecutionContext.ForceCompleted.IsCompleted)
                {
                    ExecutionContext.Debug("The task was marked as \"done\", but the process has not closed after 5 seconds. Treating the task as complete.");
                }
                else
                {
                    await step;
                }
            }
            finally
            {
                StepHost.OutputDataReceived -= OnDataReceived;
                StepHost.ErrorDataReceived -= OnDataReceived;
            }
        }

        public string GetNodeLocation()
        {
            bool useNode10 = AgentKnobs.UseNode10.GetValue(ExecutionContext).AsBoolean();
            bool taskHasNode10Data = Data is Node10HandlerData;
            bool taskHasNode16Data = Data is Node16HandlerData;
            string useNodeKnob = AgentKnobs.UseNode.GetValue(ExecutionContext).AsString();

            string nodeFolder = NodeHandler.nodeFolder;
            if (PlatformUtil.RunningOnRHEL6 && taskHasNode16Data)
            {
                Trace.Info($"Detected RedHat 6, using node 10 as execution handler, instead node16");
                nodeFolder = NodeHandler.node10Folder;
            }
            else if (taskHasNode16Data)
            {
                Trace.Info($"Task.json has node16 handler data: {taskHasNode16Data}");
                nodeFolder = NodeHandler.node16Folder;
            }
            else if (taskHasNode10Data)
            {
                Trace.Info($"Task.json has node10 handler data: {taskHasNode10Data}");
                nodeFolder = NodeHandler.node10Folder;
            }

            if (useNode10)
            {
                Trace.Info($"Found UseNode10 knob, use node10 for node tasks: {useNode10}");
                nodeFolder = NodeHandler.node10Folder;
            }

            if (!nodeHandlerHelper.IsNodeFolderExist(nodeFolder, HostContext))
            {
                string[] filteredPossibleNodeFolders = nodeHandlerHelper.GetFilteredPossibleNodeFolders(nodeFolder, possibleNodeFolders);

                if (!String.IsNullOrWhiteSpace(useNodeKnob) && filteredPossibleNodeFolders.Length > 0)
                {
                    Trace.Info($"Found UseNode knob with value \"{useNodeKnob}\", will try to find appropriate Node Runner");
                    
                    switch (useNodeKnob.ToUpper())
                    {
                        case NodeHandler.useNodeKnobLtsKey:
                            if (nodeHandlerHelper.IsNodeFolderExist(NodeHandler.nodeLTS, HostContext))
                            {
                                ExecutionContext.Warning($"Configured runner {nodeFolder} is not available, latest LTS version {NodeHandler.nodeLTS} will be used. See http://aka.ms/azdo-node-runner");
                                Trace.Info($"Found LTS version of node installed");
                                return nodeHandlerHelper.GetNodeFolderPath(NodeHandler.nodeLTS, HostContext);
                            }
                            break;
                        case NodeHandler.useNodeKnobUpgradeKey:
                            string firstExistedNodeFolder = filteredPossibleNodeFolders.FirstOrDefault(nf => nodeHandlerHelper.IsNodeFolderExist(nf, HostContext));

                            if (firstExistedNodeFolder != null)
                            {
                                ExecutionContext.Warning($"Configured runner {nodeFolder} is not available, next available version will be used. See http://aka.ms/azdo-node-runner");
                                Trace.Info($"Found {firstExistedNodeFolder} installed");
                                return nodeHandlerHelper.GetNodeFolderPath(firstExistedNodeFolder, HostContext);
                            }
                            break;
                        default:
                            Trace.Error($"Value of UseNode knob cannot be recognized");
                            break;
                    }
                }

                throw new FileNotFoundException(StringUtil.Loc("MissingNodePath", nodeHandlerHelper.GetNodeFolderPath(nodeFolder, HostContext)));
            }

            return nodeHandlerHelper.GetNodeFolderPath(nodeFolder, HostContext);
        }

        private void OnDataReceived(object sender, ProcessDataReceivedEventArgs e)
        {
            // drop any outputs after the task get force completed.
            if (ExecutionContext.ForceCompleted.IsCompleted)
            {
                return;
            }

            // This does not need to be inside of a critical section.
            // The logging queues and command handlers are thread-safe.
            if (!CommandManager.TryProcessCommand(ExecutionContext, e.Data))
            {
                ExecutionContext.Output(e.Data);
            }
        }

        private void FixVstsTaskLibModule()
        {
            // to avoid modify node_module all the time, we write a .node6 file to indicate we finsihed scan and modify.
            // the current task is good for node 6.x
            if (File.Exists(TaskDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".node6"))
            {
                Trace.Info("This task has already been scanned and corrected, no more operation needed.");
            }
            else
            {
                Trace.Info("Scan node_modules folder, looking for vsts-task-lib\\extensions.js");
                try
                {
                    foreach (var file in new DirectoryInfo(TaskDirectory).EnumerateFiles("extensions.js", SearchOption.AllDirectories))
                    {
                        if (string.Equals(file.Directory.Name, "vsts-task-lib", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(file.Directory.Name, "vso-task-lib", StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(Path.Combine(file.DirectoryName, "package.json")))
                            {
                                // read package.json, we only do the fix for 0.x->2.x
                                JObject packageJson = JObject.Parse(File.ReadAllText(Path.Combine(file.DirectoryName, "package.json")));

                                JToken versionToken;
                                if (packageJson.TryGetValue("version", StringComparison.OrdinalIgnoreCase, out versionToken))
                                {
                                    if (_vstsTaskLibVersionNeedsFix.IsMatch(versionToken.ToString()))
                                    {
                                        Trace.Info($"Fix extensions.js file at '{file.FullName}'. The vsts-task-lib version is '{versionToken.ToString()}'");

                                        // take backup of the original file
                                        File.Copy(file.FullName, Path.Combine(file.DirectoryName, "extensions.js.vstsnode5"));
                                        File.WriteAllLines(file.FullName, _extensionsNode6);
                                    }
                                }
                            }
                        }
                    }

                    File.WriteAllText(TaskDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".node6", string.Empty);
                    Trace.Info("Finished scan and correct extensions.js under vsts-task-lib");
                }
                catch (Exception ex)
                {
                    Trace.Error("Unable to scan and correct potential bug in extensions.js of vsts-task-lib.");
                    Trace.Error(ex);
                }
            }
        }
    }
}
