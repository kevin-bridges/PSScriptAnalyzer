﻿//
// Copyright (c) Microsoft Corporation.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System.Text.RegularExpressions;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.IO;
using Microsoft.Windows.PowerShell.ScriptAnalyzer.Generic;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;
using System.Management.Automation.Runspaces;
using System.Collections;

namespace Microsoft.Windows.PowerShell.ScriptAnalyzer.Commands
{
    using PSSASettings = Microsoft.Windows.PowerShell.ScriptAnalyzer.Settings;

    /// <summary>
    /// InvokeScriptAnalyzerCommand: Cmdlet to statically check PowerShell scripts.
    /// </summary>
    [Cmdlet(VerbsLifecycle.Invoke,
        "ScriptAnalyzer",
        DefaultParameterSetName = "File",
        SupportsShouldProcess = true,
        HelpUri = "https://go.microsoft.com/fwlink/?LinkId=525914")]
    public class InvokeScriptAnalyzerCommand : PSCmdlet, IOutputWriter
    {
        #region Private variables
        List<string> processedPaths;
        #endregion // Private variables

        #region Parameters
        /// <summary>
        /// Path: The path to the file or folder to invoke PSScriptAnalyzer on.
        /// </summary>
        [Parameter(Position = 0,
            ParameterSetName = "File",
            Mandatory = true,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        [ValidateNotNull]
        [Alias("PSPath")]
        public string Path
        {
            get { return path; }
            set { path = value; }
        }
        private string path;

        /// <summary>
        /// ScriptDefinition: a script definition in the form of a string to run rules on.
        /// </summary>
        [Parameter(Position = 0,
            ParameterSetName = "ScriptDefinition",
            Mandatory = true,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        [ValidateNotNull]
        public string ScriptDefinition
        {
            get { return scriptDefinition; }
            set { scriptDefinition = value; }
        }
        private string scriptDefinition;

        /// <summary>
        /// CustomRulePath: The path to the file containing custom rules to run.
        /// </summary>
        [Parameter(Mandatory = false)]
        [ValidateNotNull]
        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays")]
        [Alias("CustomizedRulePath")]
        public string[] CustomRulePath
        {
            get { return customRulePath; }
            set { customRulePath = value; }
        }
        private string[] customRulePath;

        /// <summary>
        /// RecurseCustomRulePath: Find rules within subfolders under the path
        /// </summary>
        [Parameter(Mandatory = false)]
        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays")]
        public SwitchParameter RecurseCustomRulePath
        {
            get { return recurseCustomRulePath; }
            set { recurseCustomRulePath = value; }
        }
        private bool recurseCustomRulePath;

        /// <summary>
        /// IncludeDefaultRules: Invoke default rules along with Custom rules
        /// </summary>
        [Parameter(Mandatory = false)]
        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays")]
        public SwitchParameter IncludeDefaultRules
        {
            get { return includeDefaultRules; }
            set { includeDefaultRules = value; }
        }
        private bool includeDefaultRules;

        /// <summary>
        /// ExcludeRule: Array of names of rules to be disabled.
        /// </summary>
        [Parameter(Mandatory = false)]
        [ValidateNotNull]
        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays")]
        public string[] ExcludeRule
        {
            get { return excludeRule; }
            set { excludeRule = value; }
        }
        private string[] excludeRule;

        /// <summary>
        /// IncludeRule: Array of names of rules to be enabled.
        /// </summary>
        [Parameter(Mandatory = false)]
        [ValidateNotNull]
        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays")]
        public string[] IncludeRule
        {
            get { return includeRule; }
            set { includeRule = value; }
        }
        private string[] includeRule;

        /// <summary>
        /// IncludeRule: Array of the severity types to be enabled.
        /// </summary>
        [ValidateSet("Warning", "Error", "Information", IgnoreCase = true)]
        [Parameter(Mandatory = false)]
        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays")]
        public string[] Severity
        {
            get { return severity; }
            set { severity = value; }
        }
        private string[] severity;

        /// <summary>
        /// Recurse: Apply to all files within subfolders under the path
        /// </summary>
        [Parameter(Mandatory = false)]
        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays")]
        public SwitchParameter Recurse
        {
            get { return recurse; }
            set { recurse = value; }
        }
        private bool recurse;

        /// <summary>
        /// ShowSuppressed: Show the suppressed message
        /// </summary>
        [Parameter(Mandatory = false)]
        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays")]
        public SwitchParameter SuppressedOnly
        {
            get { return suppressedOnly; }
            set { suppressedOnly = value; }
        }
        private bool suppressedOnly;

        /// <summary>
        /// Resolves rule violations automatically where possible.
        /// </summary>
        [Parameter(Mandatory = false, ParameterSetName = "File")]
        public SwitchParameter Fix
        {
            get { return fix; }
            set { fix = value; }
        }
        private bool fix;

        /// <summary>
        /// Sets the exit code to the number of warnings for usage in CI.
        /// </summary>
        [Parameter(Mandatory = false)]
        public SwitchParameter EnableExit
        {
            get { return enableExit; }
            set { enableExit = value; }
        }
        private bool enableExit;

        /// <summary>
        /// Returns path to the file that contains user profile or hash table for ScriptAnalyzer
        /// </summary>
        [Alias("Profile")]
        [Parameter(Mandatory = false)]
        [ValidateNotNull]
        public object Settings
        {
            get { return settings; }
            set { settings = value; }
        }

        private object settings;

        private bool stopProcessing;

#if !PSV3
        /// <summary>
        /// Resolve DSC resource dependency
        /// </summary>
        [Parameter(Mandatory = false)]
        public SwitchParameter SaveDscDependency
        {
            get { return saveDscDependency; }
            set { saveDscDependency = value; }
        }
        private bool saveDscDependency;
#endif // !PSV3

#if DEBUG
        /// <summary>
        /// Attaches to an instance of a .Net debugger
        /// </summary>
        [Parameter(Mandatory = false)]
        public SwitchParameter AttachAndDebug
        {
            get { return attachAndDebug; }
            set { attachAndDebug = value; }
        }
        private bool attachAndDebug = false;
#endif
        #endregion Parameters

        #region Overrides

        /// <summary>
        /// Imports all known rules and loggers.
        /// </summary>
        protected override void BeginProcessing()
        {
            // Initialize helper
#if DEBUG
            if (attachAndDebug)
            {
                if (System.Diagnostics.Debugger.IsAttached)
                {
                    System.Diagnostics.Debugger.Break();
                }
                else
                {
                    System.Diagnostics.Debugger.Launch();
                }
            }
#endif
            Helper.Instance = new Helper(
                SessionState.InvokeCommand,
                this);
            Helper.Instance.Initialize();

            var psVersionTable = this.SessionState.PSVariable.GetValue("PSVersionTable") as Hashtable;
            if (psVersionTable != null)
            {
                Helper.Instance.SetPSVersionTable(psVersionTable);
            }

            string[] rulePaths = Helper.ProcessCustomRulePaths(
                customRulePath,
                this.SessionState,
                recurseCustomRulePath);

            if (IsFileParameterSet())
            {
                ProcessPath();
            }

            string[] combRulePaths = null;
            var combRecurseCustomRulePath = RecurseCustomRulePath.IsPresent;
            var combIncludeDefaultRules = IncludeDefaultRules.IsPresent;
            try
            {
                var settingsObj = PSSASettings.Create(
                    settings,
                    processedPaths == null || processedPaths.Count == 0 ? null : processedPaths[0],
                    this);
                if (settingsObj != null)
                {
                    ScriptAnalyzer.Instance.UpdateSettings(settingsObj);

                    // For includeDefaultRules and RecurseCustomRulePath we override the value in the settings file by
                    // command line argument.
                    combRecurseCustomRulePath = OverrideSwitchParam(
                        settingsObj.RecurseCustomRulePath,
                        "RecurseCustomRulePath");
                    combIncludeDefaultRules = OverrideSwitchParam(
                        settingsObj.IncludeDefaultRules,
                        "IncludeDefaultRules");
                }

                // Ideally we should not allow the parameter to be set from settings and command line
                // simultaneously. But since, this was done before with IncludeRules, ExcludeRules and Severity,
                // we use the same strategy for CustomRulePath. So, we take the union of CustomRulePath provided in
                // the settings file and if provided on command line.
                var settingsCustomRulePath = Helper.ProcessCustomRulePaths(
                        settingsObj?.CustomRulePath?.ToArray(),
                        this.SessionState,
                        combRecurseCustomRulePath);
                    combRulePaths = rulePaths == null
                                                ? settingsCustomRulePath
                                                : settingsCustomRulePath == null
                                                    ? rulePaths
                                                    : rulePaths.Concat(settingsCustomRulePath).ToArray();
            }
            catch
            {
                this.WriteWarning(String.Format(CultureInfo.CurrentCulture, Strings.SettingsNotParsable));
                stopProcessing = true;
                return;
            }

            ScriptAnalyzer.Instance.Initialize(
                this,
                combRulePaths,
                this.includeRule,
                this.excludeRule,
                this.severity,
                combRulePaths == null || combIncludeDefaultRules,
                this.suppressedOnly);
        }

        /// <summary>
        /// Analyzes the given script/directory.
        /// </summary>
        protected override void ProcessRecord()
        {
            if (stopProcessing)
            {
                stopProcessing = false;
                return;
            }

#if !PSV3
            // TODO Support dependency resolution for analyzing script definitions
            if (saveDscDependency)
            {
                using (var rsp = RunspaceFactory.CreateRunspace())
                {
                    rsp.Open();
                    using (var moduleHandler = new ModuleDependencyHandler(rsp))
                    {
                        ScriptAnalyzer.Instance.ModuleHandler = moduleHandler;
                        this.WriteVerbose(
                            String.Format(
                                CultureInfo.CurrentCulture,
                                Strings.ModuleDepHandlerTempLocation,
                                moduleHandler.TempModulePath));
                        ProcessInput();
                    }
                }
                return;
            }
#endif
            ProcessInput();
        }

        protected override void EndProcessing()
        {
            ScriptAnalyzer.Instance.CleanUp();
            base.EndProcessing();
        }

        protected override void StopProcessing()
        {
            ScriptAnalyzer.Instance.CleanUp();
            base.StopProcessing();
        }

        #endregion

        #region Private Methods

        private void ProcessInput()
        {
            IEnumerable<DiagnosticRecord> diagnosticsList = Enumerable.Empty<DiagnosticRecord>();
            if (IsFileParameterSet())
            {
                foreach (var p in processedPaths)
                {
                    if (fix)
                    {
                        ShouldProcess(p, $"Analyzing and fixing path with Recurse={this.recurse}");
                        diagnosticsList = ScriptAnalyzer.Instance.AnalyzeAndFixPath(p, this.ShouldProcess, this.recurse);
                    }
                    else
                    {
                        ShouldProcess(p, $"Analyzing path with Recurse={this.recurse}");
                        diagnosticsList = ScriptAnalyzer.Instance.AnalyzePath(p, this.ShouldProcess, this.recurse);
                    }
                    WriteToOutput(diagnosticsList);
                }
            }
            else if (String.Equals(this.ParameterSetName, "ScriptDefinition", StringComparison.OrdinalIgnoreCase))
            {
                diagnosticsList = ScriptAnalyzer.Instance.AnalyzeScriptDefinition(scriptDefinition);
                WriteToOutput(diagnosticsList);
            }
        }

        private void WriteToOutput(IEnumerable<DiagnosticRecord> diagnosticRecords)
        {
            foreach (ILogger logger in ScriptAnalyzer.Instance.Loggers)
            {
                foreach (DiagnosticRecord diagnostic in diagnosticRecords)
                {
                    logger.LogObject(diagnostic, this);
                }
            }

            if (EnableExit.IsPresent)
            {
                this.Host.SetShouldExit(diagnosticRecords.Count());
            }
        }

        private void ProcessPath()
        {
            Collection<PathInfo> paths = this.SessionState.Path.GetResolvedPSPathFromPSPath(path);
            processedPaths = new List<string>();
            foreach (PathInfo p in paths)
            {
                processedPaths.Add(this.SessionState.Path.GetUnresolvedProviderPathFromPSPath(p.Path));
            }
        }

        private bool IsFileParameterSet()
        {
            return String.Equals(this.ParameterSetName, "File", StringComparison.OrdinalIgnoreCase);
        }

        private bool OverrideSwitchParam(bool paramValue, string paramName)
        {
            return MyInvocation.BoundParameters.ContainsKey(paramName)
                ? ((SwitchParameter)MyInvocation.BoundParameters[paramName]).ToBool()
                : paramValue;
        }

        #endregion // Private Methods
    }
}
